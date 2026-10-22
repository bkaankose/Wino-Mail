using System.IO;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ApplicationConfiguration : IApplicationConfiguration
{
    public const string SharedFolderName = "WinoShared";
    public const string OfflineDemoMarkerFileName = "wino-offline-demo";

    private string _applicationDataFolderPath;
    private string _mimeStorageFolderPath;
    private string _calendarAttachmentsFolderPath;

    public string ApplicationDataFolderPath
    {
        get => _applicationDataFolderPath;
        set
        {
            _applicationDataFolderPath = value;
            _mimeStorageFolderPath = null;
            _calendarAttachmentsFolderPath = null;
            MailAccountReauthenticationExtensions.IsOfflineDemoMode =
                !string.IsNullOrEmpty(value) && File.Exists(Path.Combine(value, OfflineDemoMarkerFileName));
        }
    }

    public string MimeStorageFolderPath => _mimeStorageFolderPath ??= EnsureSubfolder("Mime");

    public string CalendarAttachmentsFolderPath => _calendarAttachmentsFolderPath ??= EnsureSubfolder("CalendarAttachments");
    public bool AllowLegacyDataMigration { get; set; }
    public string ApplicationDisplayName { get; set; } = "Wino Mail";
    public string PublisherSharedFolderPath { get; set; }
    public string ApplicationTempFolderPath { get; set; }

    private string EnsureSubfolder(string name)
    {
        var path = Path.Combine(ApplicationDataFolderPath, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
