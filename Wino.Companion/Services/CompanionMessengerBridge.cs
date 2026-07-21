using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;

namespace Wino.Companion.Services;

/// <summary>
/// Forwards only the explicitly generated UI-message registry over AppService.
/// There is no replay queue: disconnected UI state is rebuilt from SQLite.
/// </summary>
internal sealed class CompanionMessengerBridge : IWinoRpcEventSink, IDisposable
{
    private readonly CompanionAppService appService;
    private int disposed;

    public CompanionMessengerBridge(CompanionAppService appService)
    {
        this.appService = appService;
        WinoRpcEventRegistry.RegisterCompanionToUi(WeakReferenceMessenger.Default, this);
    }

    public void Publish(object message)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            !WinoRpcEventRegistry.TrySerializeCompanionToUi(message, out var typeId, out var payload))
        {
            return;
        }

        _ = appService.PublishMessageAsync(
            new MessengerEnvelope(typeId, Encoding.UTF8.GetString(payload)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            WinoRpcEventRegistry.Unregister(WeakReferenceMessenger.Default, this);
        }
    }
}
