using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Platform.MacOS.Services;

/// <summary>Metadata supplied by the application head, without assuming an MSIX identity or a signed bundle.</summary>
public sealed class MacAppMetadataService(string appVersion, string packageName, bool isDebug) : IAppMetadataService
{
    private readonly AppTelemetryMetadata _metadata = AppTelemetryMetadata.Create(appVersion, packageName, isDebug);
    public string AppVersion => _metadata.AppVersion;
    public string PackageName => _metadata.PackageName;
    public string BuildConfiguration => _metadata.BuildConfiguration;
    public string SentryEnvironment => _metadata.SentryEnvironment;
    public string SentryRelease => _metadata.SentryRelease;
    public string SentryDist => _metadata.SentryDist;
}
