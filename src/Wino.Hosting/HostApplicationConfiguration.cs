using Wino.Core.Domain.Interfaces;

namespace Wino.Hosting;

/// <summary>
/// The folders of one host, all below its data root. The Sentry DSN is empty on purpose: an empty DSN disables the
/// SDK, so a host that runs scripts against lab data never sends crash reports or sessions as if it were an app.
/// </summary>
internal sealed class HostApplicationConfiguration : IApplicationConfiguration
{
    public required string ApplicationDataFolderPath { get; set; }
    public required string PublisherSharedFolderPath { get; set; }
    public required string ApplicationTempFolderPath { get; set; }
    public string ApplicationDisplayName => "Wino Mail";
    public bool AllowLegacyDataMigration => false;
    public string SentryDNS => string.Empty;
}
