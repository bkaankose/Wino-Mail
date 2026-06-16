using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;



#if WINDOWS_UWP
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif

namespace Wino.Services;

public class NativeAppService : INativeAppService, IAppMetadataService
{
    private string _mimeMessagesFolder = string.Empty;
    private string _editorBundlePath = string.Empty;

    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = static () => IntPtr.Zero;

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

    public async Task<string> GetEditorBundlePathAsync()
    {
        if (string.IsNullOrEmpty(_editorBundlePath))
        {
            var editorFileFromBundle = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///JS/editor.html"))
                .AsTask()
                .ConfigureAwait(false);

            _editorBundlePath = editorFileFromBundle.Path;
        }

        return _editorBundlePath;
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
        // The http/https default handler (e.g. Edge) is a Win32 desktop app. Inside this packaged,
        // self-contained WinUI 3 host the shell-activation path that Launcher.LaunchUriAsync and
        // ShellExecute funnel through silently no-ops (the API reports success but no browser opens),
        // which breaks Gmail OAuth and any other "open in browser" action. For web URLs, launch the
        // resolved default-browser executable directly via CreateProcess, which bypasses shell
        // activation entirely. Other schemes (mailto:, ms-windows-store:, custom protocols) keep
        // using the OS launcher.
        if (uri.IsAbsoluteUri
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && TryLaunchDefaultBrowser(uri))
        {
            return true;
        }

        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchDefaultBrowser(Uri uri)
    {
        try
        {
            var command = GetDefaultBrowserCommand();
            if (string.IsNullOrEmpty(command))
                return false;

            // Split the registered "shell\open\command" into executable + argument template.
            string executable;
            string argumentTemplate;

            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = command.IndexOf('"', 1);
                if (end < 0)
                    return false;

                executable = command.Substring(1, end - 1);
                argumentTemplate = command.Substring(end + 1).Trim();
            }
            else
            {
                var space = command.IndexOf(' ');
                executable = space < 0 ? command : command.Substring(0, space);
                argumentTemplate = space < 0 ? string.Empty : command.Substring(space + 1).Trim();
            }

            // MSIX/Store browsers register an AppsFolder activation token rather than a real path;
            // bail so the OS launcher fallback can handle those.
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                return false;

            var url = uri.ToString();
            var arguments = argumentTemplate.Contains("%1")
                ? argumentTemplate.Replace("%1", url)
                : argumentTemplate.Length > 0 ? $"{argumentTemplate} \"{url}\"" : $"\"{url}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return Process.Start(startInfo) != null;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDefaultBrowserCommand()
    {
        var progId = ReadUserChoiceProgId("https") ?? ReadUserChoiceProgId("http");
        if (string.IsNullOrEmpty(progId))
            return null;

        var commandSubKey = $"{progId}\\shell\\open\\command";

        using (var key = Registry.CurrentUser.OpenSubKey($"Software\\Classes\\{commandSubKey}"))
        {
            if (key?.GetValue(null) is string perUserCommand && perUserCommand.Length > 0)
                return perUserCommand;
        }

        using (var key = Registry.ClassesRoot.OpenSubKey(commandSubKey))
        {
            if (key?.GetValue(null) is string machineCommand && machineCommand.Length > 0)
                return machineCommand;
        }

        return null;
    }

    private static string ReadUserChoiceProgId(string scheme)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $"Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\{scheme}\\UserChoice");

        return key?.GetValue("ProgId") as string;
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
        => !Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().HasThreadAccess;

    public string GetCalendarAttachmentsFolderPath()
    {
        var attachmentsFolder = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
        System.IO.Directory.CreateDirectory(attachmentsFolder);
        return attachmentsFolder;
    }
}
