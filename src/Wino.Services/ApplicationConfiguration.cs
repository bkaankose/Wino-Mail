using System.IO;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ApplicationConfiguration : IApplicationConfiguration
{
    public const string SharedFolderName = "WinoShared";

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
        }
    }

    public string MimeStorageFolderPath => _mimeStorageFolderPath ??= EnsureSubfolder("Mime");

    public string CalendarAttachmentsFolderPath => _calendarAttachmentsFolderPath ??= EnsureSubfolder("CalendarAttachments");
    public bool AllowLegacyDataMigration { get; set; }
    public string ApplicationDisplayName { get; set; } = "Wino Mail";
    public string PublisherSharedFolderPath { get; set; }
    public string ApplicationTempFolderPath { get; set; }

    public string SentryDNS => "https://81365d32d74c6f223a0674a2fb7bade5@o4509722249134080.ingest.de.sentry.io/4509722259095632";

    private string EnsureSubfolder(string name)
    {
        var path = Path.Combine(ApplicationDataFolderPath, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
