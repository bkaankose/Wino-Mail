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
    /// Application insights instrumentation key.
    /// </summary>
    string SentryDNS { get; }
}
