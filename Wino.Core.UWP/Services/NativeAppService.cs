using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System;
using Windows.UI.Shell;
using Wino.Core.Domain.Interfaces;



#if WINDOWS_UWP
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.Services;

public class NativeAppService : INativeAppService
{
    private string _mimeMessagesFolder;
    private string _editorBundlePath;

    public Func<IntPtr> GetCoreWindowHwnd { get; set; }

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

    public Task<bool> LaunchUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri).AsTask();

    public string GetFullAppVersion()
    {
        Package package = Package.Current;
        PackageId packageId = package.Id;
        PackageVersion version = packageId.Version;

        return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
    }

    public async Task PinAppToTaskbarAsync()
    {
        // If Start screen manager API's aren't present
        if (!ApiInformation.IsTypePresent("Windows.UI.Shell.TaskbarManager")) return;

        // Get the taskbar manager
        var taskbarManager = TaskbarManager.GetDefault();

        // If Taskbar doesn't allow pinning, don't show the tip
        if (!taskbarManager.IsPinningAllowed) return;

        // If already pinned, don't show the tip
        if (await taskbarManager.IsCurrentAppPinnedAsync()) return;

        await taskbarManager.RequestPinCurrentAppAsync();
    }
}
