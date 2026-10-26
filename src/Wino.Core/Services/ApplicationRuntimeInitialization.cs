using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Services;

/// <summary>Checkpoints completed startup steps so retry does not repeat subscriptions.</summary>
public sealed class ApplicationRuntimeInitialization(
    Func<IDatabaseService> database,
    Func<IMailIntelligenceStore> intelligenceStore,
    Func<ITranslationService> translations,
    Func<SynchronizationManagerInitializer> synchronizationInitializer,
    Func<IKeyboardShortcutService> shortcuts,
    Func<AccountProfilePictureMaintenance> pictures,
    Func<AccountSenderPictureDirectory> senderPictures,
    Func<IWinoAccountIntelligenceSnapshotService> entitlement,
    Func<IMailIntelligenceCoordinator> intelligence,
    Func<IntelligenceResultKeyLifecycle> keys,
    Func<IPushRelayRegistrationService>? pushRelay = null,
    Func<IRelaySocketCoordinator?>? relaySocket = null,
    Func<ISyncCoordinator>? syncCoordinator = null,
    Func<IAppPolicyAcceptanceSyncService?>? policySync = null,
    Func<IEnumerable<IStartupInitializer>>? startupInitializers = null)
{
    private int _completedStep;
    private bool _startupInitializersCompleted;
    private Task _entitlementRefresh = Task.CompletedTask;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunStartupInitializersAsync(cancellationToken);
        await StepAsync(1, () => translations().InitializeAsync(), cancellationToken);
        await StepAsync(2, () => Task.WhenAll(database().InitializeAsync(), intelligenceStore().InitializeAsync(),
            translations().InitializeAsync(), synchronizationInitializer().InitializeAsync()), cancellationToken);

        // Decides timer or relay for this install (fresh: relay; upgraded with accounts: timer plus the Windows
        // introduction) and the initial per-account coverage. Runs once the database is ready and before the first
        // synchronizer is created, so nothing reads the unconfigured sync method default.
        if (syncCoordinator is not null)
            await StepAsync(3, () => syncCoordinator().InitializeAsync(cancellationToken), cancellationToken);

        await StepAsync(4, () => shortcuts().InitializeAsync(), cancellationToken);
        await StepAsync(5, () => pictures().MigrateLegacyAsync(), cancellationToken);
        await StepAsync(6, () => senderPictures().InitializeAsync(), cancellationToken);
        await StepAsync(7, async () =>
        {
            await entitlement().GetEntitlementAsync();
            _entitlementRefresh = entitlement().RefreshEntitlementAsync(cancellationToken);
        }, cancellationToken);

        // This method installs several subscriptions before returning. If it throws part
        // way through, the singleton cannot safely be retried without its own rollback.
        await StepAsync(8, async () =>
        {
            var coordinator = intelligence();
            try
            {
                await coordinator.InitializeAsync();
            }
            catch (Exception ex)
            {
                // A partial subscription failure is terminal. Dispose only this failed
                // process service, never a service still used by another shell window.
                if (coordinator is IAsyncDisposable disposable)
                {
                    try { await disposable.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception) { }
                }
                throw new RuntimeInitializationFaultException(ex);
            }
        }, cancellationToken);

        await StepAsync(9, () => keys().InitializeAsync(cancellationToken), cancellationToken);
    }

    public Task RunBackgroundStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.WhenAll(_entitlementRefresh, pictures().BackfillAsync(), StartPushRelayAsync(cancellationToken));
    }

    /// <summary>The desktop socket follows the relay registration; it starts after the registration service.</summary>
    private async Task StartRelaySocketAsync(CancellationToken cancellationToken)
    {
        if (relaySocket?.Invoke() is not { } coordinator)
            return;

        try
        {
            await coordinator.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The relay socket could not start.");
        }
    }

    /// <summary>Live notifications must never fail startup; the relay service logs its own problems.</summary>
    private async Task StartPushRelayAsync(CancellationToken cancellationToken)
    {
        if (pushRelay is null)
            return;

        try
        {
            // The sync method was decided in initialization step 3 (ISyncCoordinator), before any loop or
            // synchronizer read it; the registration service starts against the configured value.

            // Retries an app policy upload that failed earlier; never waited on, and it never throws.
            if (policySync?.Invoke() is { } policy)
                _ = policy.ReconcileAsync(cancellationToken);

            await pushRelay().StartAsync(cancellationToken);
            await StartRelaySocketAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Live notifications could not start.");
        }
    }

    /// <summary>Runs first so shared configuration is in place even when a later step such as the database fails.</summary>
    private async Task RunStartupInitializersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_startupInitializersCompleted || startupInitializers is null)
            return;

        foreach (var initializer in startupInitializers())
            await initializer.InitializeAsync();

        _startupInitializersCompleted = true;
    }

    private async Task StepAsync(int step, Func<Task> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_completedStep >= step)
            return;

        await operation();
        _completedStep = step;
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class RuntimeInitializationFaultException(Exception innerException)
    : Exception("Runtime startup failed during a service subscription step; restart the process before retrying.", innerException);
