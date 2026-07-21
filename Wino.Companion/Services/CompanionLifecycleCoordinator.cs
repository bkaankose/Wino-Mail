using Wino.Core.Domain.Interfaces;

namespace Wino.Companion.Services;

/// <summary>
/// Applies the existing close preference to a windowless process and schedules periodic
/// synchronization. No watchdog or long-running polling loop is created.
/// </summary>
internal sealed class CompanionLifecycleCoordinator : IDisposable
{
    private readonly CompanionAppService appService;
    private readonly ICompanionBackendControl backend;
    private readonly CompanionPreferences preferences;
    private readonly Action terminate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim synchronizationGate = new(1, 1);
    private readonly System.Threading.Timer synchronizationTimer;

    public CompanionLifecycleCoordinator(
        CompanionAppService appService,
        ICompanionBackendControl backend,
        CompanionPreferences preferences,
        Action terminate)
    {
        this.appService = appService;
        this.backend = backend;
        this.preferences = preferences;
        this.terminate = terminate;

        appService.ClientAttached += OnClientAttached;
        appService.ClientDetached += OnClientDetached;
        appService.PreferencesReloadRequested += OnPreferencesReloaded;
        synchronizationTimer = new System.Threading.Timer(
            static state => _ = ((CompanionLifecycleCoordinator)state!).SynchronizeOnceAsync(),
            this,
            TimeSpan.Zero,
            GetSynchronizationInterval());
        _ = EvaluateResidencyAsync();
    }

    private void OnClientAttached(object? sender, EventArgs eventArgs)
    {
        // An attached UI always keeps the companion alive, including onboarding with no accounts.
    }

    private void OnClientDetached(object? sender, EventArgs eventArgs) => _ = EvaluateResidencyAsync();

    private void OnPreferencesReloaded(object? sender, EventArgs eventArgs)
    {
        synchronizationTimer.Change(TimeSpan.Zero, GetSynchronizationInterval());
        _ = EvaluateResidencyAsync();
    }

    private async Task EvaluateResidencyAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false);
            if (appService.HasAttachedClient)
            {
                return;
            }

            var hasAccounts = await backend.HasAccountsAsync(lifetime.Token).ConfigureAwait(false);
            if (!hasAccounts || preferences.CloseBehavior == CompanionCloseBehavior.Terminate)
            {
                terminate();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task SynchronizeOnceAsync()
    {
        var cancellationToken = lifetime.Token;
        if (cancellationToken.IsCancellationRequested ||
            !await synchronizationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (preferences.CloseBehavior != CompanionCloseBehavior.Terminate &&
                await backend.HasAccountsAsync(cancellationToken).ConfigureAwait(false))
            {
                await backend.SynchronizeAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Provider errors are reported through existing synchronizer messages.
        }
        finally
        {
            synchronizationGate.Release();
        }
    }

    private TimeSpan GetSynchronizationInterval() =>
        TimeSpan.FromMinutes(Math.Max(1, preferences.EmailSyncIntervalMinutes));

    public void Dispose()
    {
        appService.ClientAttached -= OnClientAttached;
        appService.ClientDetached -= OnClientDetached;
        appService.PreferencesReloadRequested -= OnPreferencesReloaded;
        synchronizationTimer.Dispose();
        lifetime.Cancel();
        // A timer callback can already be queued while shutdown starts. Keep the token source
        // alive until process exit so that callback can observe cancellation safely.
    }
}
