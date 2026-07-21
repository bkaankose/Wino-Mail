using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Wino.AppServices.Contracts;
using Wino.AppServices.Contracts.Generated;
using Wino.Companion.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Wino.Companion;

public sealed class CompanionAppService : IDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly RpcMethodRegistry methods = new();
    private AppServiceConnection? connection;
    private AttachedClient? activeClient;
    private int disposed;

    public event EventHandler? ConnectionLost;
    public event EventHandler? ClientAttached;
    public event EventHandler? ClientDetached;
    public event EventHandler? PreferencesReloadRequested;
    public event EventHandler? ShutdownRequested;

    public bool HasAttachedClient => activeClient is not null;
    public RpcMethodRegistry Methods => methods;

    public void Prepare()
    {
        methods.Register("companion.ping.v1", static (_, _) =>
            Task.FromResult(RpcResponse.Success("{\"status\":\"ready\"}")));
        methods.Register("companion.shutdown.v1", (_, _) =>
        {
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(RpcResponse.Success());
        });
    }

    public AttachedClient? GetActiveClient() => activeClient;

    public async Task InitializeAppServiceAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseConnection();

            var newConnection = new AppServiceConnection
            {
                AppServiceName = AppServiceProtocol.ServiceName,
                PackageFamilyName = Package.Current.Id.FamilyName,
            };
            newConnection.RequestReceived += OnRequestReceived;
            newConnection.ServiceClosed += OnServiceClosed;

            var status = await newConnection.OpenAsync();
            CompanionDiagnostics.Write($"AppServiceConnection.OpenAsync returned '{status}'.");
            if (status != AppServiceConnectionStatus.Success)
            {
                newConnection.RequestReceived -= OnRequestReceived;
                newConnection.ServiceClosed -= OnServiceClosed;
                newConnection.Dispose();
                throw new InvalidOperationException($"Opening the Wino AppService failed with status '{status}'.");
            }

            connection = newConnection;
            activeClient = new AttachedClient(0, nint.Zero);
            ClientAttached?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task<bool> PublishMessageAsync(MessengerEnvelope message, CancellationToken cancellationToken = default)
    {
        var currentConnection = connection;
        if (currentConnection is null || activeClient is null)
        {
            return false;
        }

        var request = new ValueSet
        {
            [AppServiceProtocol.MessageKindKey] = AppServiceProtocol.PublishKind,
            [AppServiceProtocol.PayloadKey] = WinoAppServiceJsonContext.Serialize(message),
        };

        try
        {
            var response = await currentConnection.SendMessageAsync(request).AsTask(cancellationToken).ConfigureAwait(false);
            return response.Status == AppServiceResponseStatus.Success &&
                   response.Message.TryGetValue(AppServiceProtocol.SuccessKey, out var success) &&
                   success is true;
        }
        catch
        {
            HandleConnectionLost();
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CloseConnection();
        connectionGate.Dispose();
    }

    private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var response = await HandleRequestAsync(args.Request.Message).ConfigureAwait(false);
            await args.Request.SendResponseAsync(response);
        }
        catch (Exception exception)
        {
            CompanionDiagnostics.Write("AppService request failed.", exception);
            await args.Request.SendResponseAsync(CreateFailure(exception.Message));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<ValueSet> HandleRequestAsync(ValueSet message)
    {
        if (!TryGetString(message, AppServiceProtocol.MessageKindKey, out var kind) ||
            !TryGetString(message, AppServiceProtocol.PayloadKey, out var payload))
        {
            return CreateFailure("The AppService request envelope is invalid.");
        }

        switch (kind)
        {
            case AppServiceProtocol.InvokeKind:
            {
                var request = WinoAppServiceJsonContext.Deserialize<RpcRequest>(payload);
                var response = await InvokeAsync(request, CancellationToken.None).ConfigureAwait(false);
                return CreateSuccess(WinoAppServiceJsonContext.Serialize(response));
            }
            case AppServiceProtocol.ClientPublishKind:
            {
                var request = WinoAppServiceJsonContext.Deserialize<ClientMessengerEnvelope>(payload);
                using var messagePayload = JsonDocument.Parse(request.Message.PayloadJson);
                if (!WinoRpcEventRegistry.TryPublishUiToCompanion(
                        request.Message.MessageId,
                        messagePayload.RootElement,
                        WeakReferenceMessenger.Default))
                {
                    return CreateFailure($"Message '{request.Message.MessageId}' is not approved for companion delivery.");
                }

                return CreateSuccess(null);
            }
            case AppServiceProtocol.DetachKind:
            {
                var request = WinoAppServiceJsonContext.Deserialize<ClientDetachRequest>(payload);
                if (activeClient is not null)
                {
                    activeClient = null;
                    ClientDetached?.Invoke(this, EventArgs.Empty);
                }

                return CreateSuccess(null);
            }
            case AppServiceProtocol.ReloadPreferencesKind:
            {
                _ = WinoAppServiceJsonContext.Deserialize<PreferenceReloadRequest>(payload);
                PreferencesReloadRequested?.Invoke(this, EventArgs.Empty);
                return CreateSuccess(null);
            }
            default:
                return CreateFailure($"Unknown AppService message kind '{kind}'.");
        }
    }

    private async Task<RpcResponse> InvokeAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        RpcResponse response;
        try
        {
            if (!IsRunningProcess(request.UwpProcessId))
            {
                throw new UnauthorizedAccessException("The UWP process is no longer running.");
            }

            var windowHandle = nint.Zero;
            if (request.WindowHandle != 0)
            {
                windowHandle = (nint)request.WindowHandle;
                if (!InteractiveWindowBroker.IsValidForProcess(windowHandle, request.UwpProcessId))
                {
                    response = RpcResponse.Failure(
                        RpcErrorCode.InteractiveWindowInvalid,
                        "The supplied UWP HWND belongs to a different process.");
                    LogFailedRpc(request, response);
                    return response;
                }

            }

            activeClient = new AttachedClient(request.UwpProcessId, windowHandle);

            response = await methods.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            response = RpcResponse.Failure(RpcErrorCode.UnauthorizedClient, exception.Message);
        }
        catch (OperationCanceledException)
        {
            response = RpcResponse.Failure(RpcErrorCode.OperationCanceled, "The companion operation was canceled.");
        }
        catch (Exception exception)
        {
            CompanionDiagnostics.Write($"RPC '{request.MethodId}' threw an unhandled exception.", exception);
            response = RpcResponse.Failure(RpcErrorCode.InternalError, exception.Message);
        }

        if (!response.IsSuccess)
        {
            LogFailedRpc(request, response);
        }

        return response;
    }

    private static void LogFailedRpc(RpcRequest request, RpcResponse response) =>
        CompanionDiagnostics.Write(
            $"RPC '{request.MethodId}' failed: code={response.ErrorCode}, " +
            $"requiredAction={response.RequiredUserAction}, message={response.ErrorMessage ?? "<none>"}.");

    private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
    {
        CompanionDiagnostics.Write($"AppService connection closed with status '{args.Status}'.");
        HandleConnectionLost();
    }

    private void HandleConnectionLost()
    {
        var hadClient = activeClient is not null;
        activeClient = null;
        if (hadClient)
        {
            ClientDetached?.Invoke(this, EventArgs.Empty);
        }

        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    private void CloseConnection()
    {
        var oldConnection = Interlocked.Exchange(ref connection, null);
        if (oldConnection is null)
        {
            return;
        }

        oldConnection.RequestReceived -= OnRequestReceived;
        oldConnection.ServiceClosed -= OnServiceClosed;
        oldConnection.Dispose();
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

    private static ValueSet CreateSuccess(string? payload) => new()
    {
        [AppServiceProtocol.SuccessKey] = true,
        [AppServiceProtocol.PayloadKey] = payload ?? string.Empty,
    };

    private static ValueSet CreateFailure(string message) => new()
    {
        [AppServiceProtocol.SuccessKey] = false,
        [AppServiceProtocol.ErrorCodeKey] = RpcErrorCode.InternalError.ToString(),
        [AppServiceProtocol.ErrorMessageKey] = message,
    };

    private static bool IsRunningProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

}

public sealed record AttachedClient(int ProcessId, nint WindowHandle);
