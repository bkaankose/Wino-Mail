#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// What every CalDAV operation of an account shares: how to reach the server, which credentials
/// to use, and the lock that keeps a synchronization and a write to the same calendar apart.
/// </summary>
internal static class CalDavConnection
{
    /// <summary>How long a failed endpoint discovery is remembered before it is tried again.</summary>
    internal static readonly TimeSpan FailedDiscoveryRetryInterval = TimeSpan.FromMinutes(30);

    private static readonly ILogger Logger = Log.ForContext(typeof(CalDavConnection));
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CalendarLocks = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DiscoveryLocks = new();
    private static readonly ConcurrentDictionary<Guid, DiscoveredEndpoint> DiscoveredEndpoints = new();

    /// <summary>
    /// The configured CalDAV URL, or the one discovered from the account address. A discovery that
    /// found nothing is not repeated for <see cref="FailedDiscoveryRetryInterval"/>.
    /// </summary>
    public static async Task<Uri?> ResolveServiceUriAsync(
        MailAccount account,
        IAutoDiscoveryService autoDiscoveryService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var explicitUri = TryGetExplicitServiceUri(account);
        if (explicitUri != null)
            return explicitUri;

        if (TryGetCachedEndpoint(account.Id, out var cachedUri, out var isFresh) && isFresh)
            return cachedUri;

        var gate = DiscoveryLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (TryGetCachedEndpoint(account.Id, out cachedUri, out isFresh) && isFresh)
                return cachedUri;

            var emailCandidates = new[] { account.ServerInformation?.Address, account.Address }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Contains('@'))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Uri? discoveredUri = null;
            foreach (var email in emailCandidates)
            {
                discoveredUri = await autoDiscoveryService
                    .DiscoverCalDavServiceUriAsync(email, cancellationToken)
                    .ConfigureAwait(false);

                if (discoveredUri != null)
                    break;
            }

            DiscoveredEndpoints[account.Id] = new DiscoveredEndpoint(discoveredUri, DateTime.UtcNow);
            return discoveredUri;
        }
        finally
        {
            gate.Release();
        }
    }

    public static Uri? TryGetExplicitServiceUri(MailAccount account)
    {
        var configuredUrl = account.ServerInformation?.CalDavServiceUrl;
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return null;

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            Logger.Warning("Configured CalDAV URL is invalid for account {AccountName}: {Url}", account.Name, configuredUrl);
            return null;
        }

        return uri;
    }

    public static string ResolvePassword(MailAccount account)
    {
        var server = account.ServerInformation;

        if (!string.IsNullOrWhiteSpace(server?.CalDavPassword))
            return server.CalDavPassword;

        if (!string.IsNullOrWhiteSpace(server?.IncomingServerPassword))
            return server.IncomingServerPassword;

        if (!string.IsNullOrWhiteSpace(server?.OutgoingServerPassword))
            return server.OutgoingServerPassword;

        return string.Empty;
    }

    public static string ResolveUsername(MailAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.ServerInformation?.CalDavUsername))
            return account.ServerInformation.CalDavUsername.Trim();

        if (!string.IsNullOrWhiteSpace(account.ServerInformation?.Address))
            return account.ServerInformation.Address.Trim();

        if (!string.IsNullOrWhiteSpace(account.Address))
            return account.Address.Trim();

        return string.Empty;
    }

    /// <summary>
    /// Settings for a write. Throws when the endpoint or credentials are missing, because a write
    /// that cannot reach the server must fail visibly instead of being dropped.
    /// </summary>
    public static async Task<CalDavConnectionSettings> CreateRequiredSettingsAsync(
        MailAccount account,
        IAutoDiscoveryService autoDiscoveryService,
        CancellationToken cancellationToken,
        IDavOAuthAuthenticationProvider? oauthAuthenticationProvider = null)
    {
        var serviceUri = await ResolveServiceUriAsync(account, autoDiscoveryService, cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Cannot execute CalDAV operation because no CalDAV service URI is configured.");

        var bearerSettings = await TryCreateBearerSettingsAsync(account, serviceUri, oauthAuthenticationProvider, cancellationToken).ConfigureAwait(false);
        if (bearerSettings != null)
            return bearerSettings;

        var username = ResolveUsername(account);
        var password = ResolvePassword(account);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Cannot execute CalDAV operation because credentials are missing.");

        return new CalDavConnectionSettings
        {
            ServiceUri = serviceUri,
            Username = username,
            Password = password,
            EndpointTrust = new DavEndpointTrust
            {
                AccountId = account.Id,
                Protocol = MailServerProtocol.CalDav
            }
        };
    }

    /// <summary>
    /// Settings that send the account's OAuth token, or null when <see cref="DavOAuthRule"/> keeps CalDAV on its password.
    /// </summary>
    public static async Task<CalDavConnectionSettings?> TryCreateBearerSettingsAsync(
        MailAccount account,
        Uri serviceUri,
        IDavOAuthAuthenticationProvider? oauthAuthenticationProvider,
        CancellationToken cancellationToken)
    {
        if (oauthAuthenticationProvider == null || account.ServerInformation?.IncomingAuthenticationMethod != ImapAuthenticationMethod.OAuth2)
            return null;

        var authentication = await oauthAuthenticationProvider
            .TryCreateForAccountAsync(account.Id, account.ServerInformation, DavServiceKind.CalDav, serviceUri, cancellationToken)
            .ConfigureAwait(false);

        return authentication == null
            ? null
            : new CalDavConnectionSettings
            {
                ServiceUri = serviceUri,
                Username = ResolveUsername(account),
                Authentication = authentication
            };
    }

    public static async Task<IDisposable> LockCalendarAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        var gate = CalendarLocks.GetOrAdd(calendarId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private static bool TryGetCachedEndpoint(Guid accountId, out Uri? uri, out bool isFresh)
    {
        uri = null;
        isFresh = false;

        if (!DiscoveredEndpoints.TryGetValue(accountId, out var endpoint))
            return false;

        uri = endpoint.ServiceUri;
        isFresh = endpoint.ServiceUri != null || DateTime.UtcNow - endpoint.AttemptedAtUtc < FailedDiscoveryRetryInterval;
        return true;
    }

    private sealed record DiscoveredEndpoint(Uri? ServiceUri, DateTime AttemptedAtUtc);

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
