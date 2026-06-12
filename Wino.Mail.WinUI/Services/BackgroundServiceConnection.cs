using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc;
using Wino.Ipc.Contracts;
using Wino.Ipc.Protocol;
using Wino.Ipc.Transport;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Singleton IRpcClient for the UI process. Owns the connection to the background
/// companion: launch on demand, exponential backoff, handshake (including the
/// version-mismatch terminate/relaunch path), a ping health loop and silent crash
/// recovery — calls interrupted by a broken pipe are retried once after reconnecting;
/// writes are deduplicated companion-side by their operation id.
/// </summary>
public sealed partial class BackgroundServiceConnection : IRpcClient, IBackgroundServiceConnection, IAsyncDisposable
{
    public const int IpcProtocolVersion = 1;
    private const string BackgroundServiceApplicationId = "WinoBackgroundService";

    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExistingInstanceConnectBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LaunchedInstanceInitialConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TotalConnectBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<BackgroundServiceConnection>();

    private RpcClient? _client;
    private CancellationTokenSource? _pingLoopCts;
    private bool _isDisposed;

    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised after a silent reconnect so the shell can refresh UI state.</summary>
    public event Action? Reconnected;

    public bool IsConnected => _client is { IsConnected: true };

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(string methodName,
                                                                  TRequest request,
                                                                  JsonTypeInfo<TRequest> requestTypeInfo,
                                                                  JsonTypeInfo<TResponse> responseTypeInfo,
                                                                  Guid? operationId = null,
                                                                  CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await client.InvokeAsync(methodName, request, requestTypeInfo, responseTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (WinoRpcConnectionLostException)
        {
            // Crash recovery: reconnect and retry exactly once. Reads are idempotent and
            // writes carry an operation id that the companion deduplicates.
            client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await client.InvokeAsync(methodName, request, requestTypeInfo, responseTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InvokeAsync<TRequest>(string methodName,
                                            TRequest request,
                                            JsonTypeInfo<TRequest> requestTypeInfo,
                                            Guid? operationId = null,
                                            CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await client.InvokeAsync(methodName, request, requestTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (WinoRpcConnectionLostException)
        {
            client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await client.InvokeAsync(methodName, request, requestTypeInfo, operationId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Connects (launching the companion when needed). Used at startup and lazily by calls.
    /// </summary>
    async Task IBackgroundServiceConnection.EnsureConnectedAsync(CancellationToken cancellationToken)
        => await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

    public async Task<RpcClient> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        var existingClient = _client;
        if (existingClient is { IsConnected: true })
            return existingClient;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_client is { IsConnected: true })
                return _client;

            var wasConnectedBefore = _client != null;

            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            var client = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            _client = client;

            client.EventReceived += OnEventReceived;
            client.ConnectionClosed += OnConnectionClosed;

            StartPingLoop();

            if (wasConnectedBefore)
            {
                _logger.Information("Silently reconnected to the background service.");
                Reconnected?.Invoke();
            }

            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<RpcClient> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var pipeName = PipeNaming.GetPipeName(Package.Current.Id.FamilyName, Process.GetCurrentProcess().SessionId);
        var stopwatch = Stopwatch.StartNew();
        var backoff = TimeSpan.FromMilliseconds(100);
        var companionWasRunning = IsBackgroundServiceMutexPresent();
        var shouldLaunch = !companionWasRunning;
        var launchAttempted = false;
        var launchedBeforeFirstConnect = false;
        var attempts = 0;
        Exception? lastException = null;

        if (shouldLaunch)
        {
            _logger.Information("Background service mutex was not found; requesting activation before connecting.");
            launchAttempted = await TryLaunchBackgroundServiceAsync().ConfigureAwait(false);
            launchedBeforeFirstConnect = launchAttempted;
            shouldLaunch = false;

            if (!launchAttempted)
                throw new IOException("The Wino background service was not running and could not be launched.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                var connectTimeout = attempts == 1
                    ? launchedBeforeFirstConnect
                        ? LaunchedInstanceInitialConnectTimeout
                        : ExistingInstanceConnectBudget
                    : InitialConnectTimeout;

                var stream = await NamedPipeTransport.ConnectAsync(pipeName, connectTimeout, cancellationToken).ConfigureAwait(false);
                var client = new RpcClient(stream, WinoRpcDomainExceptions.ToException);

                var handshake = await client.HandshakeAsync(
                    new HandshakeRequest(IpcProtocolVersion, GetAppVersion(), "Wino.Mail.WinUI"),
                    cancellationToken).ConfigureAwait(false);

                if (handshake.Accepted)
                {
                    _logger.Information(
                        "Connected to the background service on pipe {PipeName} after {ElapsedMilliseconds:F0} ms and {AttemptCount} attempt(s). CompanionWasRunning={CompanionWasRunning}.",
                        pipeName,
                        stopwatch.Elapsed.TotalMilliseconds,
                        attempts,
                        companionWasRunning);

                    return client;
                }

                // Version mismatch after an app update: ask the old companion to exit,
                // then relaunch the packaged (new) one and try again.
                _logger.Warning("Background service handshake rejected: {Message}. Requesting old instance termination.", handshake.Message);

                await TryTerminateLegacyInstanceAsync(client).ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);

                launchAttempted = false;
                shouldLaunch = true;
            }
            catch (TimeoutException timeoutException)
            {
                lastException = timeoutException;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastException = exception;
            }

            if (!shouldLaunch &&
                companionWasRunning &&
                stopwatch.Elapsed >= ExistingInstanceConnectBudget)
            {
                shouldLaunch = true;
                _logger.Information(
                    "Existing background service did not accept a connection within {ElapsedMilliseconds:F0} ms; requesting activation.",
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            if (shouldLaunch && !launchAttempted)
            {
                launchAttempted = await TryLaunchBackgroundServiceAsync().ConfigureAwait(false);
            }

            if (stopwatch.Elapsed > TotalConnectBudget)
            {
                var message = $"Could not connect to the Wino background service on pipe '{pipeName}' after {stopwatch.Elapsed.TotalSeconds:F1} seconds and {attempts} attempt(s).";
                _logger.Error(lastException, message);
                throw new IOException(message, lastException);
            }

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 2000));
        }
    }

    private static bool IsBackgroundServiceMutexPresent()
    {
        try
        {
            if (Mutex.TryOpenExisting(CompanionProcessNaming.SingleInstanceMutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static async Task TryTerminateLegacyInstanceAsync(RpcClient client)
    {
        try
        {
            // Method id is stable across protocol versions by construction.
            await client.InvokeAsync(
                "IBackgroundServiceControl.TerminateAsync#0",
                new Wino.Ipc.Contracts.Generated.IBackgroundServiceControl_TerminateAsync_0Request(),
                WinoIpcJson.GetTypeInfo<Wino.Ipc.Contracts.Generated.IBackgroundServiceControl_TerminateAsync_0Request>(),
                Guid.NewGuid()).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        }
        catch
        {
            // Old instance may not understand us at all; the relaunch path handles it.
        }
    }

    private async Task<bool> TryLaunchBackgroundServiceAsync()
    {
        // The companion's app entry is hidden (AppListEntry="none"), so activate it by
        // its AUMID; GetAppListEntriesAsync does not return hidden entries.
        try
        {
            var targetAppUserModelId = $"{Package.Current.Id.FamilyName}!{BackgroundServiceApplicationId}";

            var hr = CoCreateInstance(in ApplicationActivationManagerClsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, in ApplicationActivationManagerIid, out var activationManagerPtr);
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);

            try
            {
                var activationManager = (IApplicationActivationManager)ActivationComWrappers.GetOrCreateObjectForComInstance(activationManagerPtr, System.Runtime.InteropServices.CreateObjectFlags.None);
                activationManager.ActivateApplication(targetAppUserModelId, string.Empty, 0, out _);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(activationManagerPtr);
            }

            _logger.Information("Background service launched via app activation manager.");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "App activation manager launch failed.");
        }

        try
        {
            var targetAppUserModelId = $"{Package.Current.Id.FamilyName}!{BackgroundServiceApplicationId}";
            var appEntries = await Package.Current.GetAppListEntriesAsync();
            var appEntry = appEntries.FirstOrDefault(entry =>
                string.Equals(entry.AppUserModelId, targetAppUserModelId, StringComparison.OrdinalIgnoreCase));

            if (appEntry != null && await appEntry.LaunchAsync())
            {
                _logger.Information("Background service launched via app list entry.");
                return true;
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "App list entry launch failed.");
        }

        // Fallback: start the packaged exe directly. With the WAP packaging project each
        // bundled app lives in its own subfolder of the package root.
        try
        {
            var installedLocation = Package.Current.InstalledLocation.Path;
            var exePath = Path.Combine(installedLocation, "Wino.BackgroundService", "Wino.BackgroundService.exe");

            if (!File.Exists(exePath))
            {
                exePath = Path.Combine(installedLocation, "Wino.BackgroundService.exe");
            }

            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                _logger.Information("Background service launched via Process.Start fallback.");
                return true;
            }

            _logger.Warning("Background service executable was not found in the package layout.");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to launch the background service executable.");
        }

        return false;
    }

    private void OnEventReceived(string typeName, JsonElement payload) => EventReceived?.Invoke(typeName, payload);

    private void OnConnectionClosed(Exception? fault)
    {
        _logger.Warning("Background service connection closed{Fault}.", fault == null ? string.Empty : $" ({fault.Message})");
        StopPingLoop();

        if (_isDisposed)
            return;

        // Reconnect eagerly so pushed events resume even if no call is made for a while.
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Background service reconnect failed.");
            }
        });
    }

    private void StartPingLoop()
    {
        StopPingLoop();

        var cts = new CancellationTokenSource();
        _pingLoopCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(PingInterval);

                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    var client = _client;
                    if (client == null || !client.IsConnected)
                        continue;

                    using var pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    pingTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        await client.PingAsync(pingTimeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        _logger.Warning("Background service ping failed; forcing reconnect.");
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        });
    }

    private void StopPingLoop()
    {
        _pingLoopCts?.Cancel();
        _pingLoopCts?.Dispose();
        _pingLoopCts = null;
    }

    private static string GetAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        StopPingLoop();

        if (_client != null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _connectLock.Dispose();
    }

    // Source-generated COM interop (no built-in COM marshalling) so the activation path
    // works under trimming and Native AOT.
    private static readonly Guid ApplicationActivationManagerClsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ApplicationActivationManagerIid = new("2e941141-7f97-4756-ba1d-9decde894a3d");
    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    private static readonly System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers ActivationComWrappers = new();

    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [System.Runtime.InteropServices.Marshalling.GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    internal partial interface IApplicationActivationManager
    {
        void ActivateApplication(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appUserModelId,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);
    }
}
