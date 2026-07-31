namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Singleton object that holds the application data folder path and the publisher shared folder path.
/// Load the values before calling any service.
/// App data folder is used for storing files.
/// Pubhlisher cache folder is only used for database file so other apps can access it in the same package by same publisher.
/// </summary>
public interface IApplicationConfiguration
{
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
