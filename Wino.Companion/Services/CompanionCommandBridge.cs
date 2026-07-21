using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Companion.Services;

/// <summary>
/// Executes the small, explicit set of UI commands delivered through the companion messenger.
/// </summary>
internal sealed class CompanionCommandBridge : IDisposable
{
    private readonly ISynchronizationManager synchronizationManager;

    public CompanionCommandBridge(ISynchronizationManager synchronizationManager)
    {
        this.synchronizationManager = synchronizationManager;
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this, (_, message) => _ = SynchronizeMailAsync(message));
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this, (_, message) => _ = SynchronizeCalendarAsync(message));
        WeakReferenceMessenger.Default.Register<KillAccountSynchronizerRequested>(this, (_, message) => _ = DestroySynchronizerAsync(message));
    }

    private async Task SynchronizeMailAsync(NewMailSynchronizationRequested message)
    {
        MailSynchronizationResult result;
        try
        {
            result = await synchronizationManager.SynchronizeMailAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = MailSynchronizationResult.Failed(exception);
        }

        WeakReferenceMessenger.Default.Send(new AccountSynchronizationCompleted(
            message.Options.AccountId,
            result.CompletedState,
            message.Options.GroupedSynchronizationTrackingId,
            message.Options.Type));
    }

    private async Task SynchronizeCalendarAsync(NewCalendarSynchronizationRequested message)
    {
        try
        {
            await synchronizationManager.SynchronizeCalendarAsync(message.Options).ConfigureAwait(false);
        }
        catch
        {
            // Synchronizer status messages carry the failure state to the UI.
        }
    }

    private async Task DestroySynchronizerAsync(KillAccountSynchronizerRequested message)
    {
        try
        {
            await synchronizationManager.DestroySynchronizerAsync(message.AccountId).ConfigureAwait(false);
        }
        catch
        {
            // A missing/already disposed synchronizer is equivalent to the requested state.
        }
    }

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
