using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.Messaging;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;
using Wino.Services;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.System;

namespace Wino.Mail.Uwp.Services;

public sealed class CompanionConnectionService : IWinoRpcClient
{
    // A cold companion launch covers full-trust process start, single-instance
    // arbitration and the AppService OpenAsync handshake. The very first wait is
    // therefore generous; later reconnects target an already-running companion.
    private static readonly TimeSpan FirstConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue uiDispatcherQueue;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object connectionLock = new();

    private TaskCompletionSource<AppServiceConnection> connectionSource = NewConnectionSource();
    private AppServiceConnection? connection;
    private BackgroundTaskDeferral? appServiceDeferral;
    private int foregroundAttached;
    private int hasEverConnected;

    public CompanionConnectionService(DispatcherQueue uiDispatcherQueue)
    {
        this.uiDispatcherQueue = uiDispatcherQueue
            ?? throw new ArgumentNullException(nameof(uiDispatcherQueue));
    }

    public NativeAppService NativeAppService { get; } = new();

    public event Action<ShutdownFlushRequest>? ShutdownFlushRequested;

    public bool IsForegroundAttached => Volatile.Read(ref foregroundAttached) != 0;

    public bool OnBackgroundActivated(BackgroundActivatedEventArgs args)
    {
        if (args.TaskInstance.TriggerDetails is not AppServiceTriggerDetails details)
        {
            UwpDiagnostics.Write("Background activation was not an AppService activation.");
            return false;
        }

        UwpDiagnostics.Write(
            $"AppService activation received. Name='{details.Name}', " +
            $"caller='{details.CallerPackageFamilyName}'.");
        if (!string.Equals(details.Name, AppServiceProtocol.ServiceName, StringComparison.Ordinal))
        {
            UwpDiagnostics.Write($"Rejected unexpected AppService name '{details.Name}'.");
            return false;
        }

        var deferral = args.TaskInstance.GetDeferral();
        AttachConnection(details.AppServiceConnection, deferral);
        UwpDiagnostics.Write("AppService connection attached to the UWP client.");
        return true;
    }

    public async Task<bool> ConnectAsync()
        => await ConnectCoreAsync(isForeground: true).ConfigureAwait(false);

    public async Task<bool> ConnectForBackgroundAsync()
        => await ConnectCoreAsync(isForeground: false).ConfigureAwait(false);

    private async Task<bool> ConnectCoreAsync(bool isForeground)
    {
        var currentConnection = await WaitForConnectionAsync().ConfigureAwait(false);
        if (currentConnection is null)
        {
            return false;
        }

        Volatile.Write(ref foregroundAttached, isForeground ? 1 : 0);
        return true;
    }

    public async Task PublishAsync(object message, CancellationToken cancellationToken = default)
    {
        if (!WinoRpcEventRegistry.TrySerializeUiToCompanion(message, out var messageId, out var payload))
        {
            throw new InvalidOperationException($"Message '{message.GetType().FullName}' is not approved for AppService delivery.");
        }

        var currentConnection = await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (currentConnection is null)
        {
            return;
        }

        var envelope = new ClientMessengerEnvelope(
            new MessengerEnvelope(messageId, System.Text.Encoding.UTF8.GetString(payload)));

        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await SendWithoutResponseAsync(
                    currentConnection,
                    AppServiceProtocol.ClientPublishKind,
                    envelope,
                    WinoAppServiceJsonContext.Default.ClientMessengerEnvelope,
                    cancellationToken).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                CloseConnection(currentConnection);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async Task<RpcResponse> InvokeAsync(
        string methodId,
        string? payload,
        CancellationToken cancellationToken)
    {
        var currentConnection = await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (currentConnection is null)
        {
            return CompanionUnavailable();
        }

        var request = new RpcRequest(
            Environment.ProcessId,
            IsForegroundAttached ? GetCurrentWindowHandleOrZero() : 0,
            methodId,
            payload);

        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return await SendAsync<RpcRequest, RpcResponse>(
                    currentConnection,
                    AppServiceProtocol.InvokeKind,
                    request,
                    WinoAppServiceJsonContext.Default.RpcRequest,
                    WinoAppServiceJsonContext.Default.RpcResponse,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                CloseConnection(currentConnection);
                return CompanionUnavailable(exception.Message);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(
        string methodId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request, requestTypeInfo);
        var response = await InvokeAsync(methodId, payload, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        if (string.IsNullOrWhiteSpace(response.Payload))
        {
            throw new JsonException($"Companion method '{methodId}' returned no payload.");
        }

        return JsonSerializer.Deserialize(response.Payload, responseTypeInfo)
            ?? throw new JsonException($"Companion method '{methodId}' returned a null response.");
    }

    public async Task InvokeAsync<TRequest>(
        string methodId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request, requestTypeInfo);
        var response = await InvokeAsync(methodId, payload, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task ReloadPreferencesAsync(long revision)
    {
        var currentConnection = await WaitForConnectionAsync().ConfigureAwait(false);
        if (currentConnection is null)
        {
            return;
        }

        await SendWithoutResponseAsync(
            currentConnection,
            AppServiceProtocol.ReloadPreferencesKind,
            new PreferenceReloadRequest(revision),
            WinoAppServiceJsonContext.Default.PreferenceReloadRequest,
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task DetachAsync(ClientDetachReason reason)
    {
        Volatile.Write(ref foregroundAttached, 0);
        AppServiceConnection? currentConnection;
        lock (connectionLock)
        {
            currentConnection = connection;
        }

        if (currentConnection is not null)
        {
            try
            {
                await SendWithoutResponseAsync(
                    currentConnection,
                    AppServiceProtocol.DetachKind,
                    new ClientDetachRequest(reason),
                    WinoAppServiceJsonContext.Default.ClientDetachRequest,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        CloseConnection(currentConnection);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var response = await InvokeAsync("companion.shutdown.v1", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task CompleteShutdownFlushAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new ShutdownFlushCompleted(requestId),
            WinoAppServiceJsonContext.Default.ShutdownFlushCompleted);
        var response = await InvokeAsync("companion.flush-complete.v1", payload, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public void OnCloseRequested(Windows.UI.Core.Preview.SystemNavigationCloseRequestedPreviewEventArgs args)
    {
    }

    private void AttachConnection(AppServiceConnection newConnection, BackgroundTaskDeferral deferral)
    {
        lock (connectionLock)
        {
            // WaitForConnectionAsync may already be awaiting the current source. Keep
            // that source and complete it as well as the new current source created
            // while the previous connection is closed. Replacing it first strands
            // the original waiter even though OpenAsync succeeded.
            var pendingConnectionSource = connectionSource;
            CloseConnectionCore();
            connection = newConnection;
            appServiceDeferral = deferral;
            connection.RequestReceived += OnRequestReceived;
            connection.ServiceClosed += OnServiceClosed;
            Volatile.Write(ref hasEverConnected, 1);
            pendingConnectionSource.TrySetResult(connection);
            connectionSource.TrySetResult(connection);
        }
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!TryGetString(args.Request.Message, AppServiceProtocol.MessageKindKey, out var kind) ||
                !string.Equals(kind, AppServiceProtocol.PublishKind, StringComparison.Ordinal) ||
                !TryGetString(args.Request.Message, AppServiceProtocol.PayloadKey, out var payload))
            {
                await args.Request.SendResponseAsync(CreateFailure("The UI AppService message is invalid."));
                return;
            }

            var envelope = WinoAppServiceJsonContext.Deserialize<MessengerEnvelope>(payload);
            await PublishOnUiThreadAsync(envelope).ConfigureAwait(false);
            await args.Request.SendResponseAsync(CreateSuccess());
        }
        catch (Exception exception)
        {
            await args.Request.SendResponseAsync(CreateFailure(exception.Message));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private Task PublishOnUiThreadAsync(MessengerEnvelope envelope)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!uiDispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (string.Equals(envelope.MessageId, "lifecycle.shutdown-flush-requested.v1", StringComparison.Ordinal))
                    {
                        ShutdownFlushRequested?.Invoke(
                            WinoAppServiceJsonContext.Deserialize<ShutdownFlushRequest>(envelope.PayloadJson));
                    }
                    else
                    {
                        using var payload = JsonDocument.Parse(envelope.PayloadJson);
                        WinoRpcEventRegistry.TryPublishCompanionToUi(
                            envelope.MessageId,
                            payload.RootElement,
                            WeakReferenceMessenger.Default);
                    }

                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UWP dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args) => CloseConnection(sender);

    private async Task<AppServiceConnection?> WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        Task<AppServiceConnection> task;
        lock (connectionLock)
        {
            task = connectionSource.Task;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Volatile.Read(ref hasEverConnected) != 0 ? ReconnectTimeout : FirstConnectionTimeout);
        try
        {
            return await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Companion availability is optional for all UI/read-only work. A failed
            // bounded wait must never surface as an unhandled UWP UI exception.
            UwpDiagnostics.Write("Timed out waiting for the companion AppService connection.");
            return null;
        }
    }

    private static RpcResponse CompanionUnavailable(string? detail = null) => RpcResponse.Failure(
        RpcErrorCode.CompanionUnavailable,
        string.IsNullOrWhiteSpace(detail)
            ? "The Wino companion AppService is unavailable."
            : $"The Wino companion AppService is unavailable: {detail}",
        RequiredUserAction.Retry);

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        AppServiceConnection connection,
        string kind,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var values = new ValueSet
        {
            [AppServiceProtocol.MessageKindKey] = kind,
            [AppServiceProtocol.PayloadKey] = JsonSerializer.Serialize(request, requestTypeInfo),
        };
        var response = await connection.SendMessageAsync(values).AsTask(cancellationToken).ConfigureAwait(false);
        if (response.Status != AppServiceResponseStatus.Success)
        {
            throw new InvalidOperationException($"The companion AppService returned '{response.Status}'.");
        }

        EnsureTransportSuccess(response.Message);
        if (!TryGetString(response.Message, AppServiceProtocol.PayloadKey, out var payload))
        {
            throw new InvalidOperationException("The companion AppService returned no payload.");
        }

        return JsonSerializer.Deserialize(payload, responseTypeInfo)
            ?? throw new JsonException($"The {typeof(TResponse).Name} response was null.");
    }

    private static async Task SendWithoutResponseAsync<TRequest>(
        AppServiceConnection connection,
        string kind,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        var values = new ValueSet
        {
            [AppServiceProtocol.MessageKindKey] = kind,
            [AppServiceProtocol.PayloadKey] = JsonSerializer.Serialize(request, requestTypeInfo),
        };
        var response = await connection.SendMessageAsync(values).AsTask(cancellationToken).ConfigureAwait(false);
        if (response.Status != AppServiceResponseStatus.Success)
        {
            throw new InvalidOperationException($"The companion AppService returned '{response.Status}'.");
        }

        EnsureTransportSuccess(response.Message);
    }

    private void CloseConnection(AppServiceConnection? expected)
    {
        lock (connectionLock)
        {
            if (expected is not null && !ReferenceEquals(connection, expected))
            {
                return;
            }

            CloseConnectionCore();
        }
    }

    private void CloseConnectionCore()
    {
        if (connection is not null)
        {
            connection.RequestReceived -= OnRequestReceived;
            connection.ServiceClosed -= OnServiceClosed;
            connection.Dispose();
            connection = null;
        }

        appServiceDeferral?.Complete();
        appServiceDeferral = null;
        connectionSource = NewConnectionSource();
    }

    private long GetCurrentWindowHandleOrZero()
    {
        try
        {
            return (long)NativeAppService.GetCoreWindowHwnd();
        }
        catch
        {
            return 0;
        }
    }

    private static void EnsureTransportSuccess(ValueSet response)
    {
        if (response.TryGetValue(AppServiceProtocol.SuccessKey, out var success) && success is true)
        {
            return;
        }

        var message = TryGetString(response, AppServiceProtocol.ErrorMessageKey, out var error)
            ? error
            : "The companion AppService request failed.";
        throw new InvalidOperationException(message);
    }

    private static void EnsureSuccess(RpcResponse response)
    {
        if (!response.IsSuccess)
        {
            throw new WinoRemoteException(response);
        }
    }

    private static bool TryGetString(ValueSet values, string key, out string value)
    {
        if (values.TryGetValue(key, out var rawValue) && rawValue is string text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static ValueSet CreateSuccess() => new() { [AppServiceProtocol.SuccessKey] = true };

    private static ValueSet CreateFailure(string message) => new()
    {
        [AppServiceProtocol.SuccessKey] = false,
        [AppServiceProtocol.ErrorMessageKey] = message,
    };

    private static TaskCompletionSource<AppServiceConnection> NewConnectionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}
