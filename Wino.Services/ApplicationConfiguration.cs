using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ApplicationConfiguration : IApplicationConfiguration
{
    public const string SharedFolderName = "WinoShared";

    public string ApplicationDataFolderPath { get; set; }
    public string PublisherSharedFolderPath { get; set; }
    public string ApplicationTempFolderPath { get; set; }

    public string SentryDNS => "https://81365d32d74c6f223a0674a2fb7bade5@o4509722249134080.ingest.de.sentry.io/4509722259095632";
}
