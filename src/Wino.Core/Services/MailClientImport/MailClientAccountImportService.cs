#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Services.MailClientImport;

public sealed class MailClientAccountImportService(
    IAccountService accountService,
    IKnownImapProviderCatalog providerCatalog) : IMailClientAccountImportService
{
    private const int DefaultMaxConcurrentClients = 5;

    public IReadOnlyList<MailClientImportSource> GetSources()
    {
        if (OperatingSystem.IsWindows())
            return new[] { MailClientImportSource.Thunderbird, MailClientImportSource.OutlookClassic, MailClientImportSource.NewOutlook };

        if (OperatingSystem.IsLinux())
            return new[] { MailClientImportSource.Thunderbird, MailClientImportSource.Evolution, MailClientImportSource.GnomeOnlineAccounts };

        return new[] { MailClientImportSource.Thunderbird };
    }

    public async Task<List<MailClientImportCandidate>> ScanAsync(IProgress<MailClientImportSource>? progress = null, CancellationToken cancellationToken = default)
    {
        var existingAddresses = (await accountService.GetAccountsAsync().ConfigureAwait(false))
            .Select(a => NormalizeAddress(a.Address))
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = new List<MailClientImportCandidate>();
        var indexByAddress = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var source in GetSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(source);

            List<MailClientAccountFinding> findings;
            try
            {
                findings = await Task.Run(() => ReadSource(source), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Reading the {Source} accounts for import failed.", source);
                continue;
            }

            Log.Information("Account import found {Count} accounts in {Source}.", findings.Count, source);

            foreach (var finding in findings)
            {
                if (ToCandidate(finding, existingAddresses) is not { } candidate)
                    continue;

                var key = NormalizeAddress(candidate.Address);
                if (!indexByAddress.TryGetValue(key, out var index))
                {
                    indexByAddress[key] = candidates.Count;
                    candidates.Add(candidate);
                }
                else if (Detail(candidate) > Detail(candidates[index]))
                {
                    candidates[index] = candidate;
                }
            }
        }

        return candidates;
    }

    private static List<MailClientAccountFinding> ReadSource(MailClientImportSource source) => source switch
    {
        MailClientImportSource.Thunderbird => ThunderbirdAccountReader.Read(MailClientImportLocations.GetThunderbirdRoots()),
        MailClientImportSource.OutlookClassic when OperatingSystem.IsWindows() => OutlookClassicAccountReader.Read(),
        MailClientImportSource.NewOutlook => NewOutlookAccountReader.Read(MailClientImportLocations.GetNewOutlookSettingsPath()),
        MailClientImportSource.Evolution => EvolutionAccountReader.Read(MailClientImportLocations.GetEvolutionSourceFolders()),
        MailClientImportSource.GnomeOnlineAccounts => GnomeOnlineAccountsReader.Read(MailClientImportLocations.GetGnomeOnlineAccountsPath()),
        _ => new List<MailClientAccountFinding>()
    };

    private MailClientImportCandidate? ToCandidate(MailClientAccountFinding finding, HashSet<string> existingAddresses)
    {
        var address = finding.Address.Trim();
        if (!IsAddress(address))
            return null;

        var providerType = ResolveProviderType(finding, address);
        var specialProvider = SpecialImapProvider.None;
        CustomServerInformation? serverInformation = null;

        if (providerType is MailProviderType.IMAP4 or MailProviderType.POP3)
        {
            var match = providerCatalog.Match(address, finding.Incoming?.Host ?? string.Empty);
            specialProvider = match?.SpecialImapProvider ?? SpecialImapProvider.None;

            serverInformation = finding.Incoming is { HasHost: true }
                ? CreateServerInformation(finding, providerType, address)
                : match != null ? CreateCatalogServerInformation(match, providerType, address) : null;
        }
        else if (providerType == MailProviderType.Exchange)
        {
            serverInformation = CreateExchangeServerInformation(finding, address);
        }

        var accountName = finding.AccountName.Trim();

        return new MailClientImportCandidate
        {
            Source = finding.Source,
            Address = address,
            AccountName = string.IsNullOrWhiteSpace(accountName) ? address : accountName,
            SenderName = finding.SenderName.Trim(),
            ProviderType = providerType,
            SpecialImapProvider = specialProvider,
            ServerInformation = serverInformation,
            IsAlreadyAdded = existingAddresses.Contains(NormalizeAddress(address))
        };
    }

    /// <summary>
    /// Gmail and Microsoft mailboxes become Gmail and Outlook accounts whatever protocol the other client used, so they
    /// sync through their APIs. The incoming host decides first, which also catches Google Workspace and Microsoft 365
    /// addresses on custom domains.
    /// </summary>
    private static MailProviderType ResolveProviderType(MailClientAccountFinding finding, string address)
    {
        var host = finding.Incoming?.Host ?? string.Empty;

        if (finding.Kind == MailClientAccountKind.Gmail
            || MailClientImportHosts.IsGoogleHost(host)
            || MailClientImportHosts.IsGoogleDomain(GetDomain(address)))
            return MailProviderType.Gmail;

        if (finding.Kind == MailClientAccountKind.Outlook
            || MailClientImportHosts.IsMicrosoftCloud(host)
            || MailClientImportHosts.IsMicrosoftCloud(finding.ExchangeServer)
            || MailClientImportHosts.IsMicrosoftConsumerDomain(GetDomain(address)))
            return MailProviderType.Outlook;

        return finding.Kind switch
        {
            MailClientAccountKind.Exchange => MailProviderType.Exchange,
            MailClientAccountKind.Pop3 => MailProviderType.POP3,
            _ => MailProviderType.IMAP4
        };
    }

    private static CustomServerInformation CreateServerInformation(MailClientAccountFinding finding, MailProviderType providerType, string address)
    {
        var incoming = finding.Incoming!;
        var outgoing = finding.Outgoing;
        var isPop3 = providerType == MailProviderType.POP3;

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            Address = address,
            IncomingServerType = isPop3 ? CustomIncomingServerType.POP3 : CustomIncomingServerType.IMAP4,
            IncomingServer = incoming.Host.Trim(),
            IncomingServerPort = (incoming.Port ?? DefaultIncomingPort(isPop3, incoming.Security)).ToString(),
            IncomingServerUsername = string.IsNullOrWhiteSpace(incoming.Username) ? address : incoming.Username.Trim(),
            IncomingServerPassword = string.Empty,
            IncomingServerSocketOption = incoming.Security,
            IncomingAuthenticationMethod = ToImportedAuthentication(incoming.Authentication),
            OutgoingServer = outgoing?.Host.Trim() ?? string.Empty,
            OutgoingServerPort = outgoing is { HasHost: true } ? (outgoing.Port ?? DefaultOutgoingPort(outgoing.Security)).ToString() : string.Empty,
            OutgoingServerUsername = string.IsNullOrWhiteSpace(outgoing?.Username)
                ? (string.IsNullOrWhiteSpace(incoming.Username) ? address : incoming.Username.Trim())
                : outgoing.Username.Trim(),
            OutgoingServerPassword = string.Empty,
            OutgoingServerSocketOption = outgoing?.Security ?? ImapConnectionSecurity.Auto,
            OutgoingAuthenticationMethod = ToImportedAuthentication(outgoing?.Authentication ?? ImapAuthenticationMethod.Auto),
            CalendarSupportMode = ImapCalendarSupportMode.Disabled,
            MaxConcurrentClients = DefaultMaxConcurrentClients,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
        };
    }

    private CustomServerInformation CreateCatalogServerInformation(KnownImapProviderDefinition provider, MailProviderType providerType, string address) => new()
    {
        Id = Guid.NewGuid(),
        Address = address,
        IncomingServerType = providerType == MailProviderType.POP3 ? CustomIncomingServerType.POP3 : CustomIncomingServerType.IMAP4,
        IncomingServer = provider.Incoming.Host,
        IncomingServerPort = provider.Incoming.Port.ToString(),
        IncomingServerUsername = providerCatalog.ResolveUsername(provider.Incoming.UsernamePolicy, address),
        IncomingServerPassword = string.Empty,
        IncomingServerSocketOption = provider.Incoming.Security,
        IncomingAuthenticationMethod = ToImportedAuthentication(provider.Incoming.Authentication),
        OutgoingServer = provider.Outgoing.Host,
        OutgoingServerPort = provider.Outgoing.Port.ToString(),
        OutgoingServerUsername = providerCatalog.ResolveUsername(provider.Outgoing.UsernamePolicy, address),
        OutgoingServerPassword = string.Empty,
        OutgoingServerSocketOption = provider.Outgoing.Security,
        OutgoingAuthenticationMethod = ToImportedAuthentication(provider.Outgoing.Authentication),
        CalendarSupportMode = ImapCalendarSupportMode.Disabled,
        MaxConcurrentClients = provider.MaxConcurrentClients,
        ConnectionPolicyVersion = provider.ConnectionPolicyVersion
    };

    private static CustomServerInformation CreateExchangeServerInformation(MailClientAccountFinding finding, string address) => new()
    {
        Id = Guid.NewGuid(),
        Address = address,
        IncomingServerType = CustomIncomingServerType.Exchange,
        IncomingServer = finding.ExchangeServer.Trim(),
        IncomingServerUsername = string.IsNullOrWhiteSpace(finding.Incoming?.Username) ? address : finding.Incoming.Username.Trim(),
        IncomingServerPassword = string.Empty,
        OutgoingServer = string.Empty,
        OutgoingServerPassword = string.Empty,
        ExchangeTransport = finding.ExchangeTransport,
        CalendarSupportMode = ImapCalendarSupportMode.Disabled,
        MaxConcurrentClients = DefaultMaxConcurrentClients,
        ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
    };

    /// <summary>
    /// OAuth on a generic IMAP server needs a registration profile the other client's files do not carry, so such an
    /// account is imported with a password sign-in the user can change while fixing it.
    /// </summary>
    private static ImapAuthenticationMethod ToImportedAuthentication(ImapAuthenticationMethod method)
        => method == ImapAuthenticationMethod.OAuth2 ? ImapAuthenticationMethod.Auto : method;

    private static int Detail(MailClientImportCandidate candidate)
        => candidate.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook
            ? 2
            : candidate.ServerInformation is { IncomingServer.Length: > 0 } ? 2 : 0;

    private static int DefaultIncomingPort(bool isPop3, ImapConnectionSecurity security)
        => isPop3
            ? security == ImapConnectionSecurity.SslTls ? 995 : 110
            : security == ImapConnectionSecurity.SslTls ? 993 : 143;

    private static int DefaultOutgoingPort(ImapConnectionSecurity security)
        => security == ImapConnectionSecurity.SslTls ? 465 : 587;

    private static bool IsAddress(string address)
    {
        var at = address.IndexOf('@');
        return at > 0 && at < address.Length - 1 && address.IndexOf('@', at + 1) < 0 && !address.Any(char.IsWhiteSpace);
    }

    private static string GetDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..];
    }

    private static string NormalizeAddress(string? address) => address?.Trim().ToLowerInvariant() ?? string.Empty;
}
