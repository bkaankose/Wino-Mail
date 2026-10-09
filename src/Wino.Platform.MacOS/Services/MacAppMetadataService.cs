using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Platform.MacOS.Services;

/// <summary>Metadata supplied by the application head, without assuming an MSIX identity or a signed bundle.</summary>
/// <remarks><paramref name="buildNumber"/> (CFBundleVersion) becomes the Sentry dist when present.</remarks>
public sealed class MacAppMetadataService(string appVersion, string packageName, bool isDebug, string? buildNumber = null) : IAppMetadataService
{
    private readonly AppTelemetryMetadata _metadata = AppTelemetryMetadata.Create(appVersion, packageName, isDebug);
    public string AppVersion => _metadata.AppVersion;
    public string PackageName => _metadata.PackageName;
    public string BuildConfiguration => _metadata.BuildConfiguration;
    public string SentryEnvironment => _metadata.SentryEnvironment;
    public string SentryRelease => _metadata.SentryRelease;
    public string SentryDist => string.IsNullOrWhiteSpace(buildNumber) ? _metadata.SentryDist : buildNumber.Trim();
}
