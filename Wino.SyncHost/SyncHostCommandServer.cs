using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.SyncHost;

namespace Wino.SyncHost;

internal sealed class SyncHostCommandServer
{
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly SyncHostApplication _application;
    private readonly ILogger _logger = Log.ForContext<SyncHostCommandServer>();

    public SyncHostCommandServer(
        ISynchronizationManager synchronizationManager,
        SyncHostApplication application)
    {
        _synchronizationManager = synchronizationManager;
        _application = application;
    }

    public void Start(CancellationToken cancellationToken)
        => _ = AcceptLoopAsync(cancellationToken);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                SyncHostProtocol.CommandPipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(pipe, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                _logger.Error(ex, "Failed to accept sync host command pipe client.");
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var pipeToDispose = pipe;
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var requestJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(requestJson))
            return;

        SyncHostCommandEnvelope? request = null;

        try
        {
            request = JsonSerializer.Deserialize<SyncHostCommandEnvelope>(requestJson, SyncHostJson.Options);

            if (request == null)
                throw new InvalidOperationException("Command envelope is empty.");

            var payload = await HandleCommandAsync(request, cancellationToken).ConfigureAwait(false);
            var response = new SyncHostResponseEnvelope(
                SyncHostProtocol.Version,
                request.CorrelationId,
                true,
                payload);

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, SyncHostJson.Options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sync host command failed: {CommandType}", request?.CommandType ?? "<unknown>");

            var response = new SyncHostResponseEnvelope(
                SyncHostProtocol.Version,
                request?.CorrelationId ?? Guid.Empty,
                false,
                SyncHostJson.ToElement(new object()),
                ex.GetType().Name,
                ex.Message);

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, SyncHostJson.Options)).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> HandleCommandAsync(SyncHostCommandEnvelope request, CancellationToken cancellationToken)
    {
        switch (request.CommandType)
        {
            case SyncHostProtocol.Commands.SynchronizeMail:
            {
                var options = Required<MailSynchronizationOptions>(request);
                var result = await _synchronizationManager.SynchronizeMailAsync(options, cancellationToken).ConfigureAwait(false);
                result.Exception = null;
                return SyncHostJson.ToElement(result);
            }
            case SyncHostProtocol.Commands.SynchronizeCalendar:
            {
                var options = Required<CalendarSynchronizationOptions>(request);
                var result = await _synchronizationManager.SynchronizeCalendarAsync(options, cancellationToken).ConfigureAwait(false);
                result.Exception = null;
                return SyncHostJson.ToElement(result);
            }
            case SyncHostProtocol.Commands.QueueRequests:
            {
                var payload = Required<QueueRequestsPayload>(request);
                var requests = payload.Requests
                    .Select(DeserializeRequest)
                    .Where(serializedRequest => serializedRequest != null)
                    .ToList();

                await _synchronizationManager.QueueRequestsAsync(requests, payload.AccountId, payload.TriggerSynchronization).ConfigureAwait(false);
                return SyncHostJson.ToElement(new object());
            }
            case SyncHostProtocol.Commands.IsAccountSynchronizing:
            {
                var payload = Required<AccountIdPayload>(request);
                return SyncHostJson.ToElement(_synchronizationManager.IsAccountSynchronizing(payload.AccountId));
            }
            case SyncHostProtocol.Commands.GetSynchronizationProgress:
            {
                var payload = Required<SynchronizationProgressRequestPayload>(request);
                return SyncHostJson.ToElement(_synchronizationManager.GetSynchronizationProgress(payload.AccountId, payload.Category));
            }
            case SyncHostProtocol.Commands.DownloadMimeMessage:
            {
                var payload = Required<DownloadMimeMessagePayload>(request);
                var result = await _synchronizationManager.DownloadMimeMessageAsync(payload.MailItem, payload.AccountId, cancellationToken).ConfigureAwait(false);
                return SyncHostJson.ToElement(result);
            }
            case SyncHostProtocol.Commands.DownloadCalendarAttachment:
            {
                var payload = Required<DownloadCalendarAttachmentPayload>(request);
                await _synchronizationManager
                    .DownloadCalendarAttachmentAsync(payload.CalendarItem, payload.Attachment, payload.LocalFilePath, cancellationToken)
                    .ConfigureAwait(false);

                return SyncHostJson.ToElement(new object());
            }
            case SyncHostProtocol.Commands.CancelSynchronizations:
            {
                var payload = Required<AccountIdPayload>(request);
                await _synchronizationManager.CancelSynchronizationsAsync(payload.AccountId).ConfigureAwait(false);
                return SyncHostJson.ToElement(new object());
            }
            case SyncHostProtocol.Commands.DestroySynchronizer:
            {
                var payload = Required<AccountIdPayload>(request);
                await _synchronizationManager.DestroySynchronizerAsync(payload.AccountId).ConfigureAwait(false);
                return SyncHostJson.ToElement(new object());
            }
            case SyncHostProtocol.Commands.GetPendingMailOperationIds:
            {
                var payload = Required<AccountIdPayload>(request);
                var synchronizer = await _synchronizationManager.GetSynchronizerAsync(payload.AccountId).ConfigureAwait(false);
                return SyncHostJson.ToElement(synchronizer?.GetPendingOperationUniqueIds() ?? []);
            }
            case SyncHostProtocol.Commands.GetPendingCalendarOperationIds:
            {
                var payload = Required<AccountIdPayload>(request);
                var synchronizer = await _synchronizationManager.GetSynchronizerAsync(payload.AccountId).ConfigureAwait(false);
                return SyncHostJson.ToElement(synchronizer?.GetPendingCalendarOperationIds() ?? []);
            }
            case SyncHostProtocol.Commands.OnlineSearch:
            {
                var payload = Required<OnlineSearchPayload>(request);
                var synchronizer = await _synchronizationManager.GetSynchronizerAsync(payload.AccountId).ConfigureAwait(false);

                if (synchronizer == null)
                    return SyncHostJson.ToElement(new List<Wino.Core.Domain.Entities.Mail.MailCopy>());

                var folders = payload.Folders?.Cast<Wino.Core.Domain.Models.Folders.IMailItemFolder>().ToList();
                var results = await synchronizer.OnlineSearchAsync(payload.QueryText, folders, cancellationToken).ConfigureAwait(false);
                return SyncHostJson.ToElement(results);
            }
            case SyncHostProtocol.Commands.ShutdownHost:
                _application.RequestShutdown();
                return SyncHostJson.ToElement(new object());
            default:
                throw new NotSupportedException($"Unknown sync host command: {request.CommandType}");
        }
    }

    private static T Required<T>(SyncHostCommandEnvelope envelope)
        => SyncHostJson.FromElement<T>(envelope.Payload)
           ?? throw new InvalidOperationException($"Command {envelope.CommandType} did not include a valid {typeof(T).Name} payload.");

    private static IRequestBase? DeserializeRequest(SerializedRequestPayload serializedRequest)
    {
        var requestType = Type.GetType(serializedRequest.TypeName, throwOnError: true)
                          ?? throw new InvalidOperationException($"Request type not found: {serializedRequest.TypeName}");

        return JsonSerializer.Deserialize(serializedRequest.Json, requestType, SyncHostJson.Options) as IRequestBase;
    }
}
