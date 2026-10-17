#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Gmail.v1.Data;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Google;
using Wino.Core.Http;

namespace Wino.Core.PushRelay;

/// <summary>
/// Builds relay registrations for Gmail accounts. The app owns the Gmail watch: it calls <c>users.watch</c> with the
/// relay's Pub/Sub topic (INBOX only), renews it a day before the seven-day expiry or when the relay asks, and hands
/// the relay a current access token so the relay can verify whose mailbox the watch belongs to. Nothing else leaves
/// the device.
/// </summary>
public sealed class GmailPushRelayMailboxRegistrationBuilder : IPushRelayMailboxRegistrationBuilder
{
    private static readonly TimeSpan RenewLead = TimeSpan.FromHours(24);
    private static readonly string[] InboxLabel = ["INBOX"];

    private readonly IAuthenticationProvider _authentication;
    private readonly RelayWellKnownCache _wellKnown;
    private readonly PushRelayAppManagedRegistrationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<GmailPushRelayMailboxRegistrationBuilder>();

    public GmailPushRelayMailboxRegistrationBuilder(IAuthenticationProvider authentication, RelayWellKnownCache wellKnown,
        PushRelayAppManagedRegistrationStore store, TimeProvider? timeProvider = null)
    {
        _authentication = authentication;
        _wellKnown = wellKnown;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MailProviderType ProviderType => MailProviderType.Gmail;

    public Task<bool> IsEligibleAsync(MailAccount account, CancellationToken cancellationToken = default)
        => Task.FromResult(IsEligible(account));

    private static bool IsEligible(MailAccount account)
        => account.ProviderType == MailProviderType.Gmail && account.IsMailAccessGranted && !account.IsNetworkAccessBlocked()
            && !string.IsNullOrWhiteSpace(account.Address);

    public async Task<PushRelayMailboxRegistrationRequest?> BuildAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!IsEligible(account))
            return null;

        var wellKnown = await _wellKnown.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wellKnown.GmailTopic))
        {
            _logger.Information("The relay publishes no Gmail topic; account {AccountId} stays on timers.", account.Id);
            return null;
        }

        var authenticator = _authentication.GetAuthenticator(MailProviderType.Gmail);
        var now = _timeProvider.GetUtcNow();
        var record = _store.Load(account.Id);
        var needsWatch = record is null || record.Provider != PushRelayProviders.Gmail || record.RenewalDue
            || record.ExpiresAt is not { } expiry || expiry - now < RenewLead
            || !string.Equals(record.ExternalId, account.Address, StringComparison.OrdinalIgnoreCase);

        if (needsWatch)
        {
            record = await WatchAsync(account, authenticator, wellKnown.GmailTopic, now, cancellationToken).ConfigureAwait(false);
            _store.Save(account.Id, record);
        }

        // The relay checks the token with Google's token-info endpoint and discards it; a fresh one is handed over every time.
        var token = await authenticator.GetTokenInformationAsync(account, cancellationToken: cancellationToken).ConfigureAwait(false);
        var registration = new PushRelayGmailRegistration(token.AccessToken, record!.ExpiresAt);
        var create = new PushRelayCreateMailboxRequest(PushRelayProviders.Gmail, account.Id.ToString("D"), Imap: null, Gmail: registration);
        var fingerprint = Fingerprint(account.Address, record.ExpiresAt);
        return new PushRelayMailboxRegistrationRequest(create, new PushRelayUpdateCredentialsRequest(null), fingerprint);
    }

    private async Task<AppManagedRegistrationRecord> WatchAsync(MailAccount account, IAuthenticator authenticator, string topic,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(new GmailClientMessageHandler((IGmailAuthenticator)authenticator, account));
        using var service = new GmailService(httpClient);
        var body = new WatchRequestBody { TopicName = topic, LabelIds = new List<string>(InboxLabel), LabelFilterBehavior = "INCLUDE" };
        var response = await service.Users.Watch(body, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);

        var expiresAt = response.Expiration is { } milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : now.AddDays(7);
        _logger.Information("Gmail watch registered for account {AccountId}; expires {ExpiresAt:O}.", account.Id, expiresAt);

        return new AppManagedRegistrationRecord
        {
            Provider = PushRelayProviders.Gmail,
            ExternalId = account.Address,
            HistoryId = response.HistoryId?.ToString(CultureInfo.InvariantCulture),
            ExpiresAt = expiresAt,
            RegisteredAt = now,
            RenewalDue = false
        };
    }

    private static string Fingerprint(string address, DateTimeOffset? expiresAt)
    {
        var text = string.Join('\0', PushRelayProviders.Gmail, address.ToLowerInvariant(), expiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
