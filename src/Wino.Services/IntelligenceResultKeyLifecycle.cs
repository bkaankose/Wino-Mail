#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Creates and removes the device result key as the Wino Intelligence add-on starts and ends.
/// The entitlement snapshot is the only trigger and <see cref="IntelligenceResultKeyPolicy"/>
/// is the only rule. All work is serialised, so a removal and a creation never interleave.
/// </summary>
public sealed class IntelligenceResultKeyLifecycle :
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IDisposable
{
    private readonly IIntelligenceResultKeyStore _keys;
    private readonly IMailIntelligenceCoordinator _coordinator;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlement;
    private readonly IntelligenceResultKeyPresence _presence;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<IntelligenceResultKeyLifecycle>();

    public IntelligenceResultKeyLifecycle(
        IIntelligenceResultKeyStore keys,
        IMailIntelligenceCoordinator coordinator,
        IWinoAccountIntelligenceSnapshotService entitlement,
        IntelligenceResultKeyPresence presence,
        IMessenger messenger)
    {
        _keys = keys;
        _coordinator = coordinator;
        _entitlement = entitlement;
        _presence = presence;
        _messenger = messenger;
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
    }

    /// <summary>Applies the current snapshot once at startup, before any message arrives.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => ApplyAsync(_entitlement.CurrentEntitlement, cancellationToken);

    public void Receive(WinoIntelligenceEntitlementChanged message)
        => _ = ApplySafelyAsync(message.Entitlement);

    private async Task ApplySafelyAsync(WinoIntelligenceEntitlementSnapshot snapshot)
    {
        try
        {
            await ApplyAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Intelligence must never break the app. The next snapshot tries again.
            _logger.Warning(exception, "Applying the intelligence result key policy failed for {State}.", snapshot.State);
        }
    }

    public async Task ApplyAsync(WinoIntelligenceEntitlementSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (IntelligenceResultKeyPolicy.Decide(snapshot, _presence.MayExist))
            {
                case IntelligenceResultKeyAction.Ensure:
                    await _keys.GetOrCreateAsync(snapshot.WinoAccountId!.Value, cancellationToken).ConfigureAwait(false);
                    await _coordinator.ResumeAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case IntelligenceResultKeyAction.Remove:
                    // Jobs first, while the account may still have a token to delete them on
                    // the server. Imported classifications and briefings are kept.
                    await _coordinator.AbandonJobsAsync(cancellationToken).ConfigureAwait(false);
                    await _keys.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
                    _messenger.Send(new WinoIntelligenceAccessChanged());
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _gate.Dispose();
    }
}

/// <summary>
/// A marker file that says this device may hold a result key. The key store sets it before it
/// creates a key and clears it after deleting them all. Without it the lifecycle never opens
/// the key store, so a user who never had the add-on never touches intelligence key code.
/// A stale marker is harmless: the next removal finds no rows and clears it.
/// </summary>
public class IntelligenceResultKeyPresence(IApplicationConfiguration configuration)
{
    private const string FileName = "intelligence-result-key.present";

    private string MarkerPath => Path.Combine(configuration.ApplicationDataFolderPath, FileName);

    public virtual bool MayExist => File.Exists(MarkerPath);

    public virtual void MarkPresent()
    {
        Directory.CreateDirectory(configuration.ApplicationDataFolderPath);
        if (!File.Exists(MarkerPath))
        {
            File.WriteAllBytes(MarkerPath, []);
        }
    }

    public virtual void MarkAbsent()
    {
        if (File.Exists(MarkerPath))
        {
            File.Delete(MarkerPath);
        }
    }
}
