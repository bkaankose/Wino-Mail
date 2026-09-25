namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Identity-local storage and optional read-only legacy publisher source.
/// Initialize before calling services.
/// </summary>
public interface IApplicationConfiguration
{
    bool AllowLegacyDataMigration => false;
    string ApplicationDisplayName => "Wino Mail";
    /// <summary>
    /// Application data folder.
    /// </summary>
    string ApplicationDataFolderPath { get; set; }

    /// <summary>
    /// Publisher shared folder path.
    /// </summary>
    string PublisherSharedFolderPath { get; set; }

    /// <summary>
    /// Temp folder path of the application.
    /// Files here are short-lived and can be deleted by system.
    /// </summary>
    string ApplicationTempFolderPath { get; set; }

    /// <summary>
    /// Folder under the application data folder that stores MIME messages and their resources.
    /// The folder exists after the first read.
    /// </summary>
    string MimeStorageFolderPath => EnsureDataSubfolder("Mime");

    /// <summary>
    /// Folder under the application data folder that stores downloaded calendar attachments.
    /// The folder exists after the first read.
    /// </summary>
    string CalendarAttachmentsFolderPath => EnsureDataSubfolder("CalendarAttachments");

    /// <summary>
    /// Application insights instrumentation key.
    /// </summary>
    string SentryDNS { get; }

    protected string EnsureDataSubfolder(string name)
    {
        var path = System.IO.Path.Combine(ApplicationDataFolderPath, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
