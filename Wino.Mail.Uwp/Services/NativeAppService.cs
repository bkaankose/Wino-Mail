using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Security.Authentication.Web;
using Windows.Storage;
using Windows.System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;



#if WINDOWS_UWP
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.Services;

public class NativeAppService : INativeAppService, IAppMetadataService
{
    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";
    private string _mimeMessagesFolder = string.Empty;

    public NativeAppService()
    {
        // Authentication can be requested after a ConfigureAwait(false) continuation,
        // where Window.Current/CoreWindow.GetForCurrentThread() cannot be used. Locate
        // the live CoreWindow by walking the HWND tree, as the original UWP companion did.
        GetCoreWindowHwnd = FindCurrentUwpCoreWindowHandle;
    }

    public Func<IntPtr> GetCoreWindowHwnd { get; set; }

    /// <summary>
    /// Finds this UWP process' live CoreWindow without requiring the XAML UI thread.
    /// ApplicationFrameHost owns the outer frame on Windows 10, while the nested
    /// Windows.UI.Core.CoreWindow belongs to the actual UWP process.
    /// </summary>
    public static IntPtr FindCurrentUwpCoreWindowHandle() =>
        FindUwpCoreWindowHandle((uint)Environment.ProcessId);

    private static IntPtr FindUwpCoreWindowHandle(uint processId)
    {
        for (var applicationFrame = FindWindowEx(
                 IntPtr.Zero,
                 IntPtr.Zero,
                 ApplicationFrameWindowClass,
                 null);
             applicationFrame != IntPtr.Zero;
             applicationFrame = FindWindowEx(
                 IntPtr.Zero,
                 applicationFrame,
                 ApplicationFrameWindowClass,
                 null))
        {
            var coreWindow = FindCoreWindowDescendant(applicationFrame, processId);
            if (coreWindow != IntPtr.Zero)
            {
                return coreWindow;
            }
        }

        // Newer shell configurations can expose the CoreWindow as a top-level HWND.
        for (var coreWindow = FindWindowEx(IntPtr.Zero, IntPtr.Zero, CoreWindowClass, null);
             coreWindow != IntPtr.Zero;
             coreWindow = FindWindowEx(IntPtr.Zero, coreWindow, CoreWindowClass, null))
        {
            GetWindowThreadProcessId(coreWindow, out var owningProcessId);
            if (owningProcessId == processId)
            {
                return coreWindow;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindCoreWindowDescendant(IntPtr applicationFrame, uint processId)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(applicationFrame, (windowHandle, _) =>
        {
            var className = new StringBuilder(128);
            _ = GetClassName(windowHandle, className, className.Capacity);
            if (!string.Equals(className.ToString(), CoreWindowClass, StringComparison.Ordinal))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var owningProcessId);
            if (owningProcessId != processId)
            {
                return true;
            }

            result = windowHandle;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private delegate bool EnumWindowProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr parentWindow,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumWindowProc callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    public string GetWebAuthenticationBrokerUri()
    {
#if WINDOWS_UWP
        return WebAuthenticationBroker.GetCurrentApplicationCallbackUri().AbsoluteUri;
#endif

        return string.Empty;
    }

    public async Task<string> GetMimeMessageStoragePath()
    {
        if (!string.IsNullOrEmpty(_mimeMessagesFolder))
            return _mimeMessagesFolder;

        var localFolder = ApplicationData.Current.LocalFolder;
        var mimeFolder = await localFolder.CreateFolderAsync("Mime", CreationCollisionOption.OpenIfExists);

        _mimeMessagesFolder = mimeFolder.Path;

        return _mimeMessagesFolder;
    }

    [Obsolete("This should be removed. There should be no functionality.")]
    public bool IsAppRunning()
    {
#if WINDOWS_UWP
        return (Window.Current?.Content as Frame)?.Content != null;
#endif

        return true;
    }


    public async Task LaunchFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);

        await Launcher.LaunchFileAsync(file);
    }

    public async Task<bool> LaunchUriAsync(Uri uri)
    {
        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            return false;
        }
    }

    public string GetFullAppVersion()
    {
        Package package = Package.Current;
        PackageId packageId = package.Id;
        PackageVersion version = packageId.Version;

        return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
    }

    public string AppVersion => GetFullAppVersion();

    public string PackageName => Package.Current.Id.Name;

#if DEBUG
    public string BuildConfiguration => AppTelemetryMetadata.GetBuildConfiguration(isDebug: true);
    public string SentryEnvironment => AppTelemetryMetadata.GetEnvironment(isDebug: true);
#else
    public string BuildConfiguration => AppTelemetryMetadata.GetBuildConfiguration(isDebug: false);
    public string SentryEnvironment => AppTelemetryMetadata.GetEnvironment(isDebug: false);
#endif

    public string SentryRelease => AppTelemetryMetadata.GetRelease(AppVersion);

    public string SentryDist => AppTelemetryMetadata.NormalizeAppVersion(AppVersion);

    [Obsolete("Not supported for Win SDK")]
    public async Task PinAppToTaskbarAsync()
    {
        // If Start screen manager API's aren't present
        //if (!ApiInformation.IsTypePresent("Windows.UI.Shell.TaskbarManager")) return;

        //// Get the taskbar manager
        //var taskbarManager = TaskbarManager.GetDefault();

        //// If Taskbar doesn't allow pinning, don't show the tip
        //if (!taskbarManager.IsPinningAllowed) return;

        //// If already pinned, don't show the tip
        //if (await taskbarManager.IsCurrentAppPinnedAsync()) return;

        //await taskbarManager.RequestPinCurrentAppAsync();
    }

    public bool IsAppRunningInBackground()
        => !Windows.System.DispatcherQueue.GetForCurrentThread().HasThreadAccess;

    public string GetCalendarAttachmentsFolderPath()
    {
        var attachmentsFolder = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
        System.IO.Directory.CreateDirectory(attachmentsFolder);
        return attachmentsFolder;
    }
}
