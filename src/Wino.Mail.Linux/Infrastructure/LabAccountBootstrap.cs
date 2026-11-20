#if DEBUG
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Debug only. Adds one leased IMAP lab account without the account setup screens: the plan the setup page builds for
/// a password account, run by <see cref="IAccountSetupRunner"/>, as <c>WinoHost.AddImapAccountAsync</c> does. No app
/// may reference Wino.Hosting, so the few lease fields are read here. The lab password is never logged.
/// </summary>
internal static class LabAccountBootstrap
{
    public static async Task AddAsync(IServiceProvider services, IDispatcher dispatcher, string leasePath, string serverKey)
    {
        var plan = ReadPlan(leasePath, serverKey);
        if (await services.GetRequiredService<IAccountService>().AccountAddressExistsAsync(plan.EmailAddress))
        {
            Serilog.Log.Information("The lab account {Address} exists already; the lease adds nothing.", plan.EmailAddress);
            return;
        }

        var runner = services.GetRequiredService<IAccountSetupRunner>();
        var dialogs = services.GetRequiredService<IMailDialogService>();

        Task<AccountSetupRunResult>? run = null;
        await dispatcher.ExecuteOnUIThread(() => run = runner.RunAsync(plan, null, dialogs.ShowServerCertificateTrustDialogAsync));
        var result = await run!;
        if (!result.IsCompleted || result.Account is not { } account)
            throw new InvalidOperationException($"Adding {plan.EmailAddress} failed: {result.Error?.Message ?? "the setup did not complete"}.");

        // The setup page announces the account once the runner completes; the runtime and navigation listen for it.
        await dispatcher.ExecuteOnUIThread(() => services.GetRequiredService<IMessenger>().Send(new AccountCreatedMessage(account)));
    }

    private static AccountSetupPlan ReadPlan(string leasePath, string serverKey)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
        var root = document.RootElement;
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The lease file has no accounts.");

        foreach (var item in accounts.EnumerateArray())
        {
            if (!string.Equals(Text(item, "key"), serverKey, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Text(item, "server"), serverKey, StringComparison.OrdinalIgnoreCase)) continue;

            var address = Text(item, "address");
            var name = Text(item, "name");
            var password = Text(item, "password") is { Length: > 0 } own ? own : Text(root, "password");
            var server = new CustomServerInformation
            {
                Address = address,
                DisplayName = name,
                IncomingServerType = CustomIncomingServerType.IMAP4,
                IncomingServer = Text(item, "imapHost"),
                IncomingServerPort = Number(item, "imapPort").ToString(CultureInfo.InvariantCulture),
                IncomingServerUsername = address,
                IncomingServerPassword = password,
                IncomingServerSocketOption = Parse(Text(item, "imapSecurity"), ImapConnectionSecurity.SslTls),
                IncomingAuthenticationMethod = Parse(Text(item, "imapAuth"), ImapAuthenticationMethod.NormalPassword),
                OutgoingServer = Text(item, "smtpHost"),
                OutgoingServerPort = Number(item, "smtpPort").ToString(CultureInfo.InvariantCulture),
                OutgoingServerUsername = address,
                OutgoingServerPassword = password,
                OutgoingServerSocketOption = Parse(Text(item, "smtpSecurity"), ImapConnectionSecurity.SslTls),
                OutgoingAuthenticationMethod = Parse(Text(item, "smtpAuth"), ImapAuthenticationMethod.NormalPassword),
                CalendarSupportMode = ImapCalendarSupportMode.Disabled,
                CalDavServiceUrl = string.Empty,
                CardDavServiceUrl = string.Empty,
                CalDavUsername = string.Empty,
                CalDavPassword = string.Empty,
                MaxConcurrentClients = 5,
                ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected,
                PendingCertificateTrusts = []
            };

            return new AccountSetupPlan
            {
                Kind = AccountSetupKind.Imap,
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = SpecialImapProvider.None,
                ProviderName = "IMAP",
                AccountName = address,
                AccountColorHex = string.Empty,
                InitialSynchronizationRange = InitialSynchronizationRange.Everything,
                IsMailAccessEnabled = true,
                IsCalendarAccessEnabled = false,
                IsContactAccessEnabled = false,
                IsTaskAccessEnabled = false,
                CalendarIntegrationSource = AccountIntegrationSource.Local,
                ContactIntegrationSource = AccountIntegrationSource.Local,
                TaskIntegrationSource = AccountIntegrationSource.Local,
                CalendarSupportMode = ImapCalendarSupportMode.Disabled,
                EmailAddress = address,
                DisplayName = name,
                Imap = new ImapAccountSetupData
                {
                    DisplayName = name,
                    EmailAddress = address,
                    IsMailAccessGranted = true,
                    IsCalendarAccessGranted = false,
                    ShouldAppendMessagesToSentFolder = true,
                    ServerInformation = server
                }
            };
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
