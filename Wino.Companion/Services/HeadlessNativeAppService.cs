using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Companion.Services;

/// <summary>
/// INativeAppService for the headless companion. Interactive authentication receives
/// a short-lived, validated UWP CoreWindow HWND through AppService.
/// </summary>
public class HeadlessNativeAppService : INativeAppService, IAppMetadataService
{
    private string _mimeMessagesFolder = string.Empty;

    /// <summary>
    /// The provider returns zero when no UWP client is attached, which keeps background
    /// token refresh windowless and turns UI-required auth into an attention state.
    /// </summary>
    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = null!;

    public string GetWebAuthenticationBrokerUri() => string.Empty;

    public async Task<string> GetMimeMessageStoragePath()
    {
        if (!string.IsNullOrEmpty(_mimeMessagesFolder))
            return _mimeMessagesFolder;

        var localFolder = ApplicationData.Current.LocalFolder;
        var mimeFolder = await localFolder.CreateFolderAsync("Mime", CreationCollisionOption.OpenIfExists);

        _mimeMessagesFolder = mimeFolder.Path;

        return _mimeMessagesFolder;
    }

    public bool IsAppRunning() => true;

    public async Task LaunchFileAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        await Launcher.LaunchFileAsync(file);
    }

    public Task<bool> LaunchUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri).AsTask();

    public string GetFullAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
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

    public Task PinAppToTaskbarAsync() => Task.CompletedTask;

    public string GetCalendarAttachmentsFolderPath()
    {
        var attachmentsFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
        Directory.CreateDirectory(attachmentsFolder);
        return attachmentsFolder;
    }
}
