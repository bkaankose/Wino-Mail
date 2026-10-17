#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.PushRelay;

/// <summary>
/// Builds relay registrations for Outlook accounts. The app owns the Graph subscription on the inbox: it creates it
/// with the relay's notification URLs and a random client state, renews it half a day before Graph's three-day
/// expiry or when the relay asks, and tells the relay the subscription id and client state. No token leaves the device.
/// </summary>
public sealed class GraphPushRelayMailboxRegistrationBuilder : IPushRelayMailboxRegistrationBuilder
{
    /// <summary>Graph allows at most 4230 minutes for message subscriptions.</summary>
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(4200);
    private static readonly TimeSpan RenewLead = TimeSpan.FromHours(12);
    private const string InboxResource = "/me/mailFolders('inbox')/messages";
    private const string ChangeTypes = "created,updated,deleted";
    private const string InboxFolderLabel = "INBOX";

    private readonly IAuthenticationProvider _authentication;
    private readonly RelayWellKnownCache _wellKnown;
    private readonly PushRelayAppManagedRegistrationStore _store;
    private readonly GraphSubscriptionClient _graph;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<GraphPushRelayMailboxRegistrationBuilder>();

    public GraphPushRelayMailboxRegistrationBuilder(IAuthenticationProvider authentication, RelayWellKnownCache wellKnown,
        PushRelayAppManagedRegistrationStore store, GraphSubscriptionClient? graph = null, TimeProvider? timeProvider = null)
    {
        _authentication = authentication;
        _wellKnown = wellKnown;
        _store = store;
        _graph = graph ?? new GraphSubscriptionClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MailProviderType ProviderType => MailProviderType.Outlook;

    public Task<bool> IsEligibleAsync(MailAccount account, CancellationToken cancellationToken = default)
        => Task.FromResult(IsEligible(account));

    private static bool IsEligible(MailAccount account)
        => account.ProviderType == MailProviderType.Outlook && account.IsMailAccessGranted && !account.IsNetworkAccessBlocked()
            && !string.IsNullOrWhiteSpace(account.Address);

    public async Task<PushRelayMailboxRegistrationRequest?> BuildAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!IsEligible(account))
            return null;

        var wellKnown = await _wellKnown.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wellKnown.GraphNotificationUrl))
        {
            _logger.Information("The relay publishes no Graph notification URL; account {AccountId} stays on timers.", account.Id);
            return null;
        }

        var authenticator = _authentication.GetAuthenticator(MailProviderType.Outlook);
        var now = _timeProvider.GetUtcNow();
        var record = _store.Load(account.Id);
        var valid = record is { Provider: PushRelayProviders.Graph } && !string.IsNullOrEmpty(record.ExternalId) && !string.IsNullOrEmpty(record.ClientState);

        if (!valid)
        {
            record = await CreateAsync(account, authenticator, wellKnown, now, cancellationToken).ConfigureAwait(false);
            _store.Save(account.Id, record);
        }
        else if (record!.RenewalDue || record.ExpiresAt is not { } expiry || expiry - now < RenewLead)
        {
            record = await RenewAsync(account, authenticator, wellKnown, record, now, cancellationToken).ConfigureAwait(false);
            _store.Save(account.Id, record);
        }

        var registration = new PushRelayGraphRegistration(record!.ExternalId!, record.ClientState!, record.ExpiresAt, account.Address, InboxFolderLabel, "mail");
        var create = new PushRelayCreateMailboxRequest(PushRelayProviders.Graph, account.Id.ToString("D"), Imap: null, Graph: registration);
        return new PushRelayMailboxRegistrationRequest(create, new PushRelayUpdateCredentialsRequest(null), Fingerprint(record));
    }

    private async Task<AppManagedRegistrationRecord> CreateAsync(MailAccount account, IAuthenticator authenticator, PushRelayWellKnown wellKnown,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var clientState = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)); // 64 chars, under the relay's 128 limit
        var request = new GraphSubscriptionCreateRequest(ChangeTypes, wellKnown.GraphNotificationUrl!, wellKnown.GraphLifecycleUrl, InboxResource,
            now + SubscriptionLifetime, clientState);
        var response = await _graph.CreateAsync(account, authenticator, request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.Id))
            throw new InvalidOperationException("Graph created a subscription without an id.");

        _logger.Information("Graph subscription created for account {AccountId}; expires {ExpiresAt:O}.", account.Id, response.ExpirationDateTime);
        return new AppManagedRegistrationRecord
        {
            Provider = PushRelayProviders.Graph,
            ExternalId = response.Id,
            ClientState = clientState,
            ExpiresAt = response.ExpirationDateTime ?? now + SubscriptionLifetime,
            RegisteredAt = now,
            RenewalDue = false
        };
    }

    private async Task<AppManagedRegistrationRecord> RenewAsync(MailAccount account, IAuthenticator authenticator, PushRelayWellKnown wellKnown,
        AppManagedRegistrationRecord record, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _graph.RenewAsync(account, authenticator, record.ExternalId!, now + SubscriptionLifetime, cancellationToken).ConfigureAwait(false);
            record.ExpiresAt = response.ExpirationDateTime ?? now + SubscriptionLifetime;
            record.RenewalDue = false;
            _logger.Information("Graph subscription renewed for account {AccountId}; expires {ExpiresAt:O}.", account.Id, record.ExpiresAt);
            return record;
        }
        catch (GraphSubscriptionException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone)
        {
            // Expired subscriptions cannot be renewed; Graph wants a new one.
            _logger.Information("Graph subscription for account {AccountId} is gone ({Status}); creating a new one.", account.Id, (int)ex.StatusCode);
            return await CreateAsync(account, authenticator, wellKnown, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Fingerprint(AppManagedRegistrationRecord record)
    {
        var text = string.Join('\0', PushRelayProviders.Graph, record.ExternalId, record.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
