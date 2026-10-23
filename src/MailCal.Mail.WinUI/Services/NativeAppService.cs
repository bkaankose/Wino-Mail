using MailCal.Mail.WinUI.Models;
using MailCal.Mail.WinUI.Interfaces;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using MailCal.Core.Domain.Models.Platform;
using Microsoft.UI.Input;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Enums;

using MailCal.Helpers;
using MailCal.Mail.WinUI.Extensions;



#if WINDOWS_UWP
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif

namespace MailCal.Services;

/// <summary>
/// Single owner of the stateless Windows platform capabilities the app needs: launching, clipboard,
/// keyboard state, startup task, WebView2 probe, notification sound, taskbar and shell presence state.
/// </summary>
public partial class NativeAppService : INativeAppService, IAppMetadataService, IUserPresenceStateProvider, IExternalLauncher, IClipboardService, IShortcutPlatformService, ITaskCompletionSound
{
    private readonly DispatcherQueue? _platformDispatcher = DispatcherQueue.GetForCurrentThread();

    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;

    void ITaskCompletionSound.Play() => PlayTaskCompletionSound();

    async Task<PlatformOperationResult> IExternalLauncher.LaunchFileAsync(string path, CancellationToken cancellationToken)
        => await RunPlatformOperationAsync(async () => await LaunchFileAsync(path), cancellationToken);

    async Task<PlatformOperationResult> IExternalLauncher.LaunchUriAsync(Uri uri, CancellationToken cancellationToken)
        => await RunPlatformOperationAsync(async () =>
        {
            if (!await LaunchUriAsync(uri))
                throw new InvalidOperationException("Windows declined to open the URI.");
        }, cancellationToken);

    async Task<PlatformOperationResult> IClipboardService.CopyTextAsync(string text, CancellationToken cancellationToken)
        => await RunPlatformOperationAsync(() => ExecuteOnPlatformThreadAsync(async () =>
        {
            await CopyClipboardAsync(text);
            return true;
        }, cancellationToken), cancellationToken);

    private static async Task<PlatformOperationResult> RunPlatformOperationAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
            return new(PlatformOperationStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            return new(PlatformOperationStatus.Cancelled);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new(PlatformOperationStatus.Unavailable, ex.Message);
        }
        catch (Exception ex)
        {
            return new(PlatformOperationStatus.Failed, ex.Message);
        }
    }

    internal async Task<T> ExecuteOnPlatformThreadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_platformDispatcher == null)
            throw new PlatformNotSupportedException("The Windows platform dispatcher is unavailable.");

        if (_platformDispatcher.HasThreadAccess)
            return await operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_platformDispatcher.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            throw new PlatformNotSupportedException("The Windows platform dispatcher is shutting down.");
        }

        // Accepted native work must finish before its caller releases associated state.
        return await completion.Task;
    }
    private const uint AbmGetTaskbarPosition = 0x00000005;
    private const string MailCalStartupTaskId = "MailCalStartupId";

    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = static () => IntPtr.Zero;

    public string GetWebAuthenticationBrokerUri()
    {
#if WINDOWS_UWP
        return WebAuthenticationBroker.GetCurrentApplicationCallbackUri().AbsoluteUri;
#endif

        return string.Empty;
    }

    public async Task LaunchFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);

        if (!await Launcher.LaunchFileAsync(file))
            throw new InvalidOperationException("Windows declined to open the file.");
    }

    public async Task<bool> LaunchUriAsync(Uri uri)
    {
        // Launcher (ShellExecute) lets Windows hand the address to the default browser or browser picker
        // through its registered command. Pass the escaped form: Uri.ToString() decodes %20 into spaces,
        // which the receiving command line splits. Launcher must be called from the UI (ASTA) thread, and
        // the Gmail sign-in reaches this method from a background thread.
        var target = uri.IsAbsoluteUri ? new Uri(uri.AbsoluteUri) : uri;

        try
        {
            return await ExecuteOnPlatformThreadAsync(async () => await Launcher.LaunchUriAsync(target), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows could not launch a {Scheme} address.", target.IsAbsoluteUri ? target.Scheme : "relative");
            return false;
        }
    }

    public string AppVersion
    {
        get
        {
            var version = Package.Current.Id.Version;
            return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
        }
    }

    public string PackageName => Package.Current.Id.Name;

#if DEBUG
    public string BuildConfiguration => "Debug";
#else
    public string BuildConfiguration => "Release";
#endif

    public Task CopyClipboardAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        Clipboard.SetContent(package);

        return Task.CompletedTask;
    }

    public bool IsCtrlKeyPressed()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    public bool IsShiftKeyPressed()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    public async Task<StartupBehaviorResult> ToggleStartupBehavior(bool isEnabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(MailCalStartupTaskId);

            if (isEnabled)
            {
                await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }

            // The session-start trigger follows the startup task's state.
            await MailCal.Mail.WinUI.Activation.SessionStartActivation.SyncRegistrationAsync();
        }
        catch (Exception)
        {
            Log.Error("Error toggling startup behavior");
        }

        return await GetCurrentStartupBehaviorAsync();
    }

    public async Task<StartupBehaviorResult> GetCurrentStartupBehaviorAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(MailCalStartupTaskId);

            return task.State.AsStartupBehaviorResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting startup behavior");

            return StartupBehaviorResult.Fatal;
        }
    }

    public async Task<bool> IsWebView2RuntimeAvailableAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            await WebViewExtensions.GetSharedEnvironmentAsync();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 runtime validation failed.");
            return false;
        }
    }

    public void PlayTaskCompletionSound() => NotificationSoundPlayer.Play(NotificationSoundEvent.Default);

    #region IUserPresenceStateProvider

    // Reads the shell notification state. There is no WinUI API for Focus assist, so this is the
    // supported Win32 route. The interface stays separate so notification policy remains unit-testable.

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out UserNotificationState state);

    public bool IsPresenting()
    {
        var state = GetUserNotificationState();

        return state is UserNotificationState.PresentationMode
            or UserNotificationState.RunningDirect3dFullScreen
            or UserNotificationState.Busy;
    }

    public bool IsSystemQuietTimeActive() => GetUserNotificationState() == UserNotificationState.QuietTime;

    private static UserNotificationState GetUserNotificationState()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 ? state : UserNotificationState.AcceptsNotifications;
        }
        catch (Exception)
        {
            // The shell call is unavailable in some session states. Treat it as "nothing special is
            // happening" rather than suppressing every notification.
            return UserNotificationState.AcceptsNotifications;
        }
    }

    #endregion

    public WindowsTaskbarPosition GetTaskbarPosition()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
        };

        if (SHAppBarMessage(AbmGetTaskbarPosition, ref data) == UIntPtr.Zero ||
            !Enum.IsDefined(typeof(WindowsTaskbarPosition), (int)data.uEdge))
        {
            return WindowsTaskbarPosition.Bottom;
        }

        return (WindowsTaskbarPosition)data.uEdge;
    }

    public bool IsAppRunningInBackground()
        => !Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().HasThreadAccess;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}
