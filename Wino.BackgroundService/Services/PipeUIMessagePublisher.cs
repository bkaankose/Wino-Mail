using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc.Transport;
using Wino.Ipc.Contracts.Generated;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Companion-side publisher: every UI message is dispatched into the local messenger
/// (companion-internal recipients keep working) and forwarded as an event frame to
/// every connected UI client.
/// </summary>
public sealed class PipeUIMessagePublisher : IUIMessagePublisher
{
    private readonly NamedPipeRpcServerHost _serverHost;

    public PipeUIMessagePublisher(NamedPipeRpcServerHost serverHost)
    {
        _serverHost = serverHost;
    }

    public void Publish<TMessage>(TMessage message) where TMessage : class, IUIMessage
    {
        WeakReferenceMessenger.Default.Send(message);

        try
        {
            if (WinoRpcEventRegistry.TrySerialize(message, out var typeName, out var payload))
            {
                _serverHost.PublishEvent(typeName, payload);
            }
            else
            {
                Log.Warning("UI message type {MessageType} has no event registry entry and was not forwarded.", typeof(TMessage).Name);
            }
        }
        catch (System.Exception exception)
        {
            Log.Error(exception, "Failed to forward UI message {MessageType} over the pipe.", typeof(TMessage).Name);
        }
    }
}
