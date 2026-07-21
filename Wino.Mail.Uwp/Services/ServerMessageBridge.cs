using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Server;

namespace Wino.Mail.Uwp.Services;

/// <summary>
/// Keeps Wino.Messages.Server directional: ViewModels publish commands locally and
/// this bridge publishes only the approved command messages to the companion messenger.
/// </summary>
internal sealed class ServerMessageBridge : IDisposable
{
    private readonly CompanionConnectionService connection;

    public ServerMessageBridge(CompanionConnectionService connection)
    {
        this.connection = connection;
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this, (_, message) => _ = ForwardAsync(message));
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this, (_, message) => _ = ForwardAsync(message));
        WeakReferenceMessenger.Default.Register<KillAccountSynchronizerRequested>(this, (_, message) => _ = ForwardAsync(message));
    }

    private async Task ForwardAsync(object message)
    {
        try
        {
            await connection.PublishAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // The initiating ViewModel observes synchronizer state and can request again.
        }
    }

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
