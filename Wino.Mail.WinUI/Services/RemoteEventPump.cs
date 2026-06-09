using System;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Serilog;
using Wino.Ipc.Contracts.Generated;
using Wino.Messaging.Client.Accounts;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Receives event frames pushed by the background companion, deserializes them through the
/// generated event registry and re-publishes them into the local WeakReferenceMessenger on
/// the UI dispatcher so existing IRecipient registrations in ViewModels work untouched.
/// </summary>
public sealed class RemoteEventPump : IDisposable
{
    private readonly BackgroundServiceConnection _connection;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger _logger = Log.ForContext<RemoteEventPump>();

    public RemoteEventPump(BackgroundServiceConnection connection, DispatcherQueue dispatcherQueue)
    {
        _connection = connection;
        _dispatcherQueue = dispatcherQueue;

        _connection.EventReceived += OnEventReceived;
        _connection.Reconnected += OnReconnected;
    }

    private void OnEventReceived(string typeName, JsonElement payload)
    {
        // Clone: the element's backing document is owned by the IPC layer.
        var payloadClone = payload.Clone();

        var enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!WinoRpcEventRegistry.TryPublishToMessenger(typeName, payloadClone, WeakReferenceMessenger.Default))
                {
                    _logger.Warning("Forwarded UI message {TypeName} has no registry entry.", typeName);
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to publish forwarded UI message {TypeName}.", typeName);
            }
        });

        if (!enqueued)
        {
            _logger.Warning("Could not enqueue forwarded UI message {TypeName} to the dispatcher.", typeName);
        }
    }

    private void OnReconnected()
    {
        // The companion may have restarted; refresh menus and unread state.
        _dispatcherQueue.TryEnqueue(() =>
            WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(AutomaticallyNavigateFirstItem: false)));
    }

    public void Dispose()
    {
        _connection.EventReceived -= OnEventReceived;
        _connection.Reconnected -= OnReconnected;
    }
}
