using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI.Services;

internal sealed class NotificationHostClient : INotificationHostClient, IDisposable
{
    private static readonly Guid ActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ActivationManagerInterfaceId = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumHostLifetime = TimeSpan.FromSeconds(35);
    private readonly string _localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
    private readonly Lazy<NotificationHostProcessSupervisor> _supervisor = new(() => new NotificationHostProcessSupervisor());
    private int _disposed;

    public NotificationHostClient()
    {
        try
        {
            _ = NotificationHostFileStore.CleanupStaleFiles(_localCachePath, StaleEnvelopeAge);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean stale notification host envelopes.");
        }
    }

    public Task ShowAsync(
        NotificationHostApplication application,
        AppNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.Show,
                application,
                notification.Payload,
                notification.Tag,
                notification.Group),
            cancellationToken);
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        return DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.RemoveByTag,
                application,
                null,
                tag,
                null),
            cancellationToken);
    }

    private async Task DispatchAsync(NotificationHostRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var requestId = Guid.NewGuid();
        await NotificationHostFileStore
            .WriteRequestAsync(_localCachePath, requestId, request, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var supervisor = _supervisor.Value;
            if (Volatile.Read(ref _disposed) != 0)
            {
                supervisor.Dispose();
                throw new ObjectDisposedException(nameof(NotificationHostClient));
            }

            var applicationId = NotificationHostApplicationIds.GetApplicationId(request.Application);
            var appUserModelId = $"{Package.Current.Id.FamilyName}!{applicationId}";
            var launchedAtUtc = DateTime.UtcNow;
            var processId = ActivateApplication(appUserModelId, NotificationHostLaunchArguments.CreateRequest(requestId));
            using var process = TryOpenHost(processId, request.Application, launchedAtUtc);
            if (process == null)
            {
                if (File.Exists(NotificationHostPaths.GetRequestPath(_localCachePath, requestId)))
                    throw new InvalidOperationException($"Notification host exited before consuming request {requestId:N}.");

                return;
            }

            var exitCode = await supervisor.SuperviseAsync(process, MaximumHostLifetime, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"Notification host request {requestId:N} exited with code {exitCode}.");
        }
        catch
        {
            NotificationHostFileStore.TryDeleteRequest(_localCachePath, requestId);
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (_supervisor.IsValueCreated)
            _supervisor.Value.Dispose();
    }

    private static Process? TryOpenHost(uint processId, NotificationHostApplication application, DateTime launchedAtUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(checked((int)processId));
        }
        catch (ArgumentException)
        {
            // A one-shot host can finish before ActivateApplication returns.
            return null;
        }

        try
        {
            _ = process.SafeHandle;
            if (process.HasExited)
            {
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Notification host exited with code {process.ExitCode}.");

                process.Dispose();
                return null;
            }

            var executableName = application switch
            {
                NotificationHostApplication.Mail => "Wino.Mail.NotificationHost.exe",
                NotificationHostApplication.Calendar => "Wino.Calendar.NotificationHost.exe",
                NotificationHostApplication.People => "Wino.People.NotificationHost.exe",
                NotificationHostApplication.Tasks => "Wino.Tasks.NotificationHost.exe",
                _ => throw new ArgumentOutOfRangeException(nameof(application))
            };
            var expectedPath = Path.Combine(Package.Current.InstalledLocation.Path, executableName);
            if (process.StartTime.ToUniversalTime() < launchedAtUtc ||
                !string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                // Never attach or terminate a different process if the returned PID was recycled.
                throw new InvalidOperationException("Activated notification host process identity did not match the request.");
            }

            return process;
        }
        catch (Exception ex) when ((ex is Win32Exception || ex is InvalidOperationException) && process.HasExited)
        {
            // The host may finish while its executable path or creation time is being queried.
            var exitCode = process.ExitCode;
            process.Dispose();
            if (exitCode != 0)
                throw new InvalidOperationException($"Notification host exited with code {exitCode}.", ex);

            return null;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static unsafe uint ActivateApplication(string appUserModelId, string arguments)
    {
        var classId = ActivationManagerClassId;
        var interfaceId = ActivationManagerInterfaceId;
        var result = CoCreateInstance(ref classId, IntPtr.Zero, 5, ref interfaceId, out var instance);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            var virtualTable = *(void***)instance;
            var activateApplication = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, uint, uint*, int>)virtualTable[3];

            fixed (char* appUserModelIdPointer = appUserModelId)
            fixed (char* argumentsPointer = arguments)
            {
                uint processId = 0;
                result = activateApplication(instance, appUserModelIdPointer, argumentsPointer, 0, &processId);
                Marshal.ThrowExceptionForHR(result);
                return processId;
            }
        }
        finally
        {
            _ = Marshal.Release(instance);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}
