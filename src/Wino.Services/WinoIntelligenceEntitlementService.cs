#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoIntelligenceEntitlementService :
    IWinoIntelligenceEntitlementService,
    IRecipient<WinoAccountSignedInMessage>,
    IRecipient<WinoAccountSignedOutMessage>,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IDisposable
{
    private readonly IWinoAccountIntelligenceSnapshotService _snapshotService;
    private readonly IWinoAccountSessionService _sessions;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private WinoIntelligenceEntitlementSnapshot _current =
        WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow);

    public WinoIntelligenceEntitlementService(
        IWinoAccountIntelligenceSnapshotService snapshotService,
        IWinoAccountSessionService sessions,
        IMessenger messenger)
    {
        _snapshotService = snapshotService;
        _sessions = sessions;
        _messenger = messenger;
        messenger.Register<WinoAccountSignedInMessage>(this);
        messenger.Register<WinoAccountSignedOutMessage>(this);
        messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
    }

    public WinoIntelligenceEntitlementSnapshot Current => Volatile.Read(ref _current);

    public async Task<WinoIntelligenceEntitlementSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
            return SetCurrent(WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow));

        var cached = await _snapshotService.GetCachedAsync(session.AccountId, cancellationToken).ConfigureAwait(false);
        var evaluated = WinoIntelligenceEntitlementSnapshot.Evaluate(
            session.AccountId, cached?.Billing, cached?.Usage, DateTimeOffset.UtcNow, isFreshBilling: false);
        if (!await _sessions.IsCurrentAsync(session, cancellationToken).ConfigureAwait(false))
            return Current;

        return SetCurrent(evaluated);
    }

    public async Task<WinoIntelligenceEntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (session is null)
                return SetCurrent(WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow));

            var result = await _snapshotService.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!await _sessions.IsCurrentAsync(session, cancellationToken).ConfigureAwait(false))
                return Current;

            var billingIsFresh = result?.BillingRefreshed == true;
            var evaluated = WinoIntelligenceEntitlementSnapshot.Evaluate(
                session.AccountId, result?.Snapshot.Billing, result?.Snapshot.Usage,
                DateTimeOffset.UtcNow, billingIsFresh);
            return SetCurrent(evaluated);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void SetSignedOut()
        => SetCurrent(WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow));

    private WinoIntelligenceEntitlementSnapshot SetCurrent(WinoIntelligenceEntitlementSnapshot value)
    {
        var previous = Interlocked.Exchange(ref _current, value);
        if (previous.State != value.State || previous.WinoAccountId != value.WinoAccountId)
            _messenger.Send(new WinoIntelligenceEntitlementChanged(value));

        return value;
    }

    public void Receive(WinoAccountSignedInMessage message) => _ = RefreshAsync();

    public void Receive(WinoAccountSignedOutMessage message) => SetSignedOut();

    public void Receive(WinoAccountProfileUpdatedMessage message) => _ = RefreshAsync();

    public void Receive(WinoIntelligenceEntitlementChanged message)
        => Interlocked.Exchange(ref _current, message.Entitlement);

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _refreshLock.Dispose();
    }
}
