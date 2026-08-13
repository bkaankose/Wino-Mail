using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Services;

public sealed class WinoTelemetryContextProvider(
    IPreferencesService preferencesService,
    IStatePersistanceService statePersistenceService,
    IAppMetadataService appMetadataService) : IWinoTelemetryContextProvider
{
    public WinoTelemetryContextSnapshot GetCurrent()
        => new(
            preferencesService.DiagnosticId,
            statePersistenceService.ApplicationMode.ToString().ToLowerInvariant(),
            appMetadataService.AppVersion,
            appMetadataService.PackageName,
            appMetadataService.BuildConfiguration,
            appMetadataService.SentryEnvironment,
            appMetadataService.SentryRelease,
            appMetadataService.SentryDist,
            preferencesService.IsLoggingEnabled);
}
