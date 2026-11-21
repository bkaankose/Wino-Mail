#if DEBUG
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Debug only. Adds one leased IMAP lab account without the account setup screens: the plan the setup page builds for
/// a password account with calendars, contacts and tasks through the lab server's CalDAV and CardDAV service, run by
/// <see cref="IAccountSetupRunner"/>, as <c>WinoHost.AddImapAccountAsync</c> does with <c>LabAccountModes.AllThroughDav</c>.
/// No app may reference Wino.Hosting, so the few lease fields are read here. The lab password is never logged.
/// </summary>
internal static class LabAccountBootstrap
{
    public static async Task AddAsync(IServiceProvider services, IDispatcher dispatcher, string leasePath, string serverKey)
    {
        var lease = ReadLease(leasePath, serverKey);
        if (await services.GetRequiredService<IAccountService>().AccountAddressExistsAsync(lease.Address))
        {
            Serilog.Log.Information("The lab account {Address} exists already; the lease adds nothing.", lease.Address);
            return;
        }

        var plan = BuildPlan(lease);
        var runner = services.GetRequiredService<IAccountSetupRunner>();
        var dialogs = services.GetRequiredService<IMailDialogService>();

        Task<AccountSetupRunResult>? run = null;
        await dispatcher.ExecuteOnUIThread(() => run = runner.RunAsync(plan, null, dialogs.ShowServerCertificateTrustDialogAsync));
        if (run is null) throw new InvalidOperationException("The account setup did not start on the UI thread.");
        var result = await run;
        if (!result.IsCompleted || result.Account is not { } account)
            throw new InvalidOperationException($"Adding {plan.EmailAddress} failed: {result.Error?.Message ?? "the setup did not complete"}.");

        // The setup page announces the account once the runner completes; the runtime and navigation listen for it.
        await dispatcher.ExecuteOnUIThread(() => services.GetRequiredService<IMessenger>().Send(new AccountCreatedMessage(account)));
    }

    /// <summary>
    /// Turns on CalDAV calendars and tasks and CardDAV contacts for the existing account of the lease, the way the
    /// IMAP and CalDAV settings page saves an edit, so lane profiles made before this change keep their mail.
    /// </summary>
    public static async Task EnableDavAsync(IServiceProvider services, IDispatcher dispatcher, string leasePath, string serverKey)
    {
        var lease = ReadLease(leasePath, serverKey);
        var accounts = services.GetRequiredService<IAccountService>();
        var existing = (await accounts.GetAccountsAsync())
            .FirstOrDefault(item => string.Equals(item.Address, lease.Address, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Serilog.Log.Warning("The lab account {Address} does not exist; there is nothing to give calendars and contacts.", lease.Address);
            return;
        }

        var account = await accounts.GetAccountAsync(existing.Id);
        if (account.ServerInformation is not { } current)
            throw new InvalidOperationException($"The lab account {lease.Address} has no server settings.");

        var server = BuildServer(lease);
        server.Id = current.Id;
        server.AccountId = account.Id;
        server.ConnectionPolicyVersion = current.ConnectionPolicyVersion;

        account.IsCalendarAccessGranted = true;
        account.IsCalendarAccessEnabled = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Dav;
        account.IsContactAccessGranted = true;
        account.IsContactAccessEnabled = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Dav;
        account.IsTaskAccessGranted = true;
        account.IsTaskAccessEnabled = true;
        account.TaskIntegrationSource = AccountIntegrationSource.Dav;
        account.AttentionReason = AccountAttentionReason.None;

        await accounts.UpdateImapConnectionSettingsAsync(account, server);
        await services.GetRequiredService<IAccountCapabilityService>().EnsureLocalCapabilityStoresAsync(account);
        await services.GetRequiredService<ISynchronizationManager>().DestroySynchronizerAsync(account.Id);

        await dispatcher.ExecuteOnUIThread(() =>
        {
            var messenger = services.GetRequiredService<IMessenger>();
            messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
            messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarEvents
            }));
            messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Full
            }));
            messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = account.Id,
                Type = TaskSynchronizationType.Full
            }));
        });
        Serilog.Log.Information("The lab account {Address} now syncs calendars, contacts and tasks through DAV.", lease.Address);
    }

    private sealed record LeaseAccount(string Address, string Name, string Password, JsonElement Item);

    private static AccountSetupPlan BuildPlan(LeaseAccount lease) => new()
    {
        Kind = AccountSetupKind.Imap,
        ProviderType = MailProviderType.IMAP4,
        SpecialImapProvider = SpecialImapProvider.None,
        ProviderName = "IMAP",
        AccountName = lease.Address,
        AccountColorHex = string.Empty,
        InitialSynchronizationRange = InitialSynchronizationRange.Everything,
        IsMailAccessEnabled = true,
        IsCalendarAccessEnabled = true,
        IsContactAccessEnabled = true,
        IsTaskAccessEnabled = true,
        CalendarIntegrationSource = AccountIntegrationSource.Dav,
        ContactIntegrationSource = AccountIntegrationSource.Dav,
        TaskIntegrationSource = AccountIntegrationSource.Dav,
        CalendarSupportMode = ImapCalendarSupportMode.CalDav,
        EmailAddress = lease.Address,
        DisplayName = lease.Name,
        Imap = new ImapAccountSetupData
        {
            DisplayName = lease.Name,
            EmailAddress = lease.Address,
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true,
            ShouldAppendMessagesToSentFolder = true,
            ServerInformation = BuildServer(lease)
        }
    };

    private static CustomServerInformation BuildServer(LeaseAccount lease)
    {
        var item = lease.Item;
        var davUrl = Text(item, "davUrl");
        var cardDavUrl = Text(item, "cardDavUrl");
        if (davUrl.Length == 0 || cardDavUrl.Length == 0)
            throw new InvalidOperationException($"The lab server of {lease.Address} has no CalDAV or CardDAV service.");

        return new CustomServerInformation
        {
            Address = lease.Address,
            DisplayName = lease.Name,
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingServer = Text(item, "imapHost"),
            IncomingServerPort = Number(item, "imapPort").ToString(CultureInfo.InvariantCulture),
            IncomingServerUsername = lease.Address,
            IncomingServerPassword = lease.Password,
            IncomingServerSocketOption = Parse(Text(item, "imapSecurity"), ImapConnectionSecurity.SslTls),
            IncomingAuthenticationMethod = Parse(Text(item, "imapAuth"), ImapAuthenticationMethod.NormalPassword),
            OutgoingServer = Text(item, "smtpHost"),
            OutgoingServerPort = Number(item, "smtpPort").ToString(CultureInfo.InvariantCulture),
            OutgoingServerUsername = lease.Address,
            OutgoingServerPassword = lease.Password,
            OutgoingServerSocketOption = Parse(Text(item, "smtpSecurity"), ImapConnectionSecurity.SslTls),
            OutgoingAuthenticationMethod = Parse(Text(item, "smtpAuth"), ImapAuthenticationMethod.NormalPassword),
            CalendarSupportMode = ImapCalendarSupportMode.CalDav,
            CalDavServiceUrl = davUrl,
            CardDavServiceUrl = cardDavUrl,
            CalDavUsername = Text(item, "davUsername") is { Length: > 0 } user ? user : lease.Address,
            CalDavPassword = lease.Password,
            MaxConcurrentClients = 5,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected,
            PendingCertificateTrusts = []
        };
    }

    private static LeaseAccount ReadLease(string leasePath, string serverKey)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
        var root = document.RootElement;
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The lease file has no accounts.");

        foreach (var item in accounts.EnumerateArray())
        {
            if (!string.Equals(Text(item, "key"), serverKey, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Text(item, "server"), serverKey, StringComparison.OrdinalIgnoreCase)) continue;

            var password = Text(item, "password") is { Length: > 0 } own ? own : Text(root, "password");
            return new LeaseAccount(Text(item, "address"), Text(item, "name"), password, item.Clone());
        }

        throw new InvalidOperationException($"The lease has no account on the '{serverKey}' server.");
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static TEnum Parse<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
#endif
