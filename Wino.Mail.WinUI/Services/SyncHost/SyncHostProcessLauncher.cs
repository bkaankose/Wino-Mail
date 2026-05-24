using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Wino.Messaging.SyncHost;

namespace Wino.Mail.WinUI.Services.SyncHost;

public sealed class SyncHostProcessLauncher
{
    private const string SyncHostParameterGroupId = "SyncHost";
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<SyncHostProcessLauncher>();

    public void StartInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureRunningAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to start sync host in the background.");
            }
        });
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await CanConnectAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false))
            return;

        await _launchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await CanConnectAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false))
                return;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                await LaunchSyncHostAsync(cancellationToken).ConfigureAwait(false);

                if (await WaitUntilConnectableAsync(cancellationToken).ConfigureAwait(false))
                    return;

                _logger.Warning("Sync host command pipe was not available after launch attempt {Attempt}.", attempt);
            }

            throw new TimeoutException("Wino sync host did not open the command pipe after launch.");
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    private async Task LaunchSyncHostAsync(CancellationToken cancellationToken)
    {
        if (HasPackageIdentity())
        {
            await FullTrustProcessLauncher
                .LaunchFullTrustProcessForCurrentAppAsync(SyncHostParameterGroupId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Started background sync host using packaged full-trust activation.");
            return;
        }

        var hostPath = Path.Combine(AppContext.BaseDirectory, "Wino.SyncHost.exe");

        if (!File.Exists(hostPath))
            throw new FileNotFoundException("Sync host executable was not found.", hostPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = "--background",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        _logger.Information("Started background sync host from {HostPath}.", hostPath);
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id.FullName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilConnectableAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CanConnectAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
                return true;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                SyncHostProtocol.CommandPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            return pipe.IsConnected;
        }
        catch
        {
            return false;
        }
    }
}
