using System;
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
    Func<IntelligenceResultKeyLifecycle> keys)
{
    private int _completedStep;
    private Task _entitlementRefresh = Task.CompletedTask;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StepAsync(1, () => translations().InitializeAsync(), cancellationToken);
        await StepAsync(2, () => Task.WhenAll(database().InitializeAsync(), intelligenceStore().InitializeAsync(),
            translations().InitializeAsync(), synchronizationInitializer().InitializeAsync()), cancellationToken);
        await StepAsync(3, () => shortcuts().InitializeAsync(), cancellationToken);
        await StepAsync(4, () => pictures().MigrateLegacyAsync(), cancellationToken);
        await StepAsync(5, () => senderPictures().InitializeAsync(), cancellationToken);
        await StepAsync(6, async () =>
        {
            await entitlement().GetEntitlementAsync();
            _entitlementRefresh = entitlement().RefreshEntitlementAsync(cancellationToken);
        }, cancellationToken);

        // This method installs several subscriptions before returning. If it throws part
        // way through, the singleton cannot safely be retried without its own rollback.
        await StepAsync(7, async () =>
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

        await StepAsync(8, () => keys().InitializeAsync(cancellationToken), cancellationToken);
    }

    public Task RunBackgroundStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.WhenAll(_entitlementRefresh, pictures().BackfillAsync());
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
