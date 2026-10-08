namespace Wino.Core.Domain.Models.Telemetry;

public sealed record WinoTelemetryContextSnapshot(
    string DiagnosticId,
    string AppMode,
    string AppVersion,
    string PackageName,
    string BuildConfiguration,
    string SentryEnvironment,
    string SentryRelease,
    string SentryDist,
    bool IsTelemetryEnabled);
