using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Serilog;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.SyncHost;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Services.SyncHost;

public sealed class SyncHostEventBridge : IAsyncDisposable
{
    private readonly SyncHostProcessLauncher _processLauncher;
    private readonly ILogger _logger = Log.ForContext<SyncHostEventBridge>();
    private readonly SemaphoreSlim _startSemaphore = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private DispatcherQueue? _dispatcherQueue;

    public SyncHostEventBridge(SyncHostProcessLauncher processLauncher)
    {
        _processLauncher = processLauncher;
    }

    public void Start()
    {
        if (_cancellationTokenSource != null)
            return;

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _cancellationTokenSource = new CancellationTokenSource();
        _ = RunAsync(_cancellationTokenSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellationTokenSource == null)
            return;

        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await _startSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _processLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
                    await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Sync host event bridge disconnected.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _startSemaphore.Release();
        }
    }

    private async Task ReadEventsAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            SyncHostProtocol.EventPipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(pipe);

        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
                return;

            var envelope = JsonSerializer.Deserialize<SyncHostEventEnvelope>(json, SyncHostJson.Options);

            if (envelope == null)
                continue;

            DispatchEvent(envelope);
        }
    }

    private void DispatchEvent(SyncHostEventEnvelope envelope)
    {
        void Send()
        {
            try
            {
                SendCore(envelope);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispatch sync host event {EventType}.", envelope.EventType);
            }
        }

        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            Send();
            return;
        }

        if (_dispatcherQueue?.TryEnqueue(Send) != true)
        {
            Send();
        }
    }

    private static void SendCore(SyncHostEventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case SyncHostProtocol.Events.AccountSynchronizationProgressUpdated:
                Send<AccountSynchronizationProgressUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.AccountSynchronizationCompleted:
                Send<AccountSynchronizationCompleted>(envelope);
                break;
            case SyncHostProtocol.Events.AccountSynchronizerStateChanged:
                Send<AccountSynchronizerStateChanged>(envelope);
                break;
            case SyncHostProtocol.Events.SynchronizationActionsAdded:
                Send<SynchronizationActionsAdded>(envelope);
                break;
            case SyncHostProtocol.Events.SynchronizationActionsCompleted:
                Send<SynchronizationActionsCompleted>(envelope);
                break;
            case SyncHostProtocol.Events.RefreshUnreadCounts:
                Send<RefreshUnreadCountsMessage>(envelope);
                break;
            case SyncHostProtocol.Events.AccountFolderConfigurationUpdated:
                Send<AccountFolderConfigurationUpdated>(envelope);
                break;
            case SyncHostProtocol.Events.AccountCacheReset:
                Send<AccountCacheResetMessage>(envelope);
                break;
            case SyncHostProtocol.Events.MailAdded:
                Send<MailAddedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.BulkMailAdded:
                Send<BulkMailAddedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.MailRemoved:
                Send<MailRemovedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.BulkMailRemoved:
                Send<BulkMailRemovedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.MailUpdated:
                Send<MailUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.BulkMailUpdated:
                Send<BulkMailUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.MailStateUpdated:
                Send<MailStateUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.BulkMailStateUpdated:
                Send<BulkMailStateUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.MailDownloaded:
                Send<MailDownloadedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.DraftCreated:
                Send<DraftCreated>(envelope);
                break;
            case SyncHostProtocol.Events.DraftFailed:
                Send<DraftFailed>(envelope);
                break;
            case SyncHostProtocol.Events.DraftMapped:
                Send<DraftMapped>(envelope);
                break;
            case SyncHostProtocol.Events.FolderRenamed:
                Send<FolderRenamed>(envelope);
                break;
            case SyncHostProtocol.Events.FolderDeleted:
                Send<FolderDeleted>(envelope);
                break;
            case SyncHostProtocol.Events.FolderSynchronizationEnabled:
                Send<FolderSynchronizationEnabled>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarItemAdded:
                Send<CalendarItemAdded>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarItemUpdated:
                Send<CalendarItemUpdated>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarItemDeleted:
                Send<CalendarItemDeleted>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarListAdded:
                Send<CalendarListAdded>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarListUpdated:
                Send<CalendarListUpdated>(envelope);
                break;
            case SyncHostProtocol.Events.CalendarListDeleted:
                Send<CalendarListDeleted>(envelope);
                break;
            case SyncHostProtocol.Events.AccountCreated:
                Send<AccountCreatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.AccountUpdated:
                Send<AccountUpdatedMessage>(envelope);
                break;
            case SyncHostProtocol.Events.AccountRemoved:
                Send<AccountRemovedMessage>(envelope);
                break;
        }
    }

    private static void Send<TMessage>(SyncHostEventEnvelope envelope)
        where TMessage : class
    {
        var message = SyncHostJson.FromElement<TMessage>(envelope.Payload);

        if (message != null)
        {
            WeakReferenceMessenger.Default.Send(message);
        }
    }
}
