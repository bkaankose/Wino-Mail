using System.IO;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Services;

public class ApplicationConfiguration : IApplicationConfiguration
{
    public const string SharedFolderName = "MailCalShared";
    public const string OfflineDemoMarkerFileName = "mailcal-offline-demo";

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
    public string ApplicationDisplayName { get; set; } = "Mail-Cal";
    public string PublisherSharedFolderPath { get; set; }
    public string ApplicationTempFolderPath { get; set; }

    private string EnsureSubfolder(string name)
    {
        var path = Path.Combine(ApplicationDataFolderPath, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
