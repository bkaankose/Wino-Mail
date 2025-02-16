using Microsoft.ApplicationInsights.Extensibility;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Misc;

namespace Wino.Services;

public class LogInitializer : ILogInitializer
{
    private readonly LoggingLevelSwitch _levelSwitch = new LoggingLevelSwitch();
    private readonly IPreferencesService _preferencesService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly TelemetryConfiguration _telemetryConfiguration;

    public LogInitializer(IPreferencesService preferencesService, IApplicationConfiguration applicationConfiguration)
    {
        _preferencesService = preferencesService;
        _applicationConfiguration = applicationConfiguration;

        _telemetryConfiguration = new TelemetryConfiguration(applicationConfiguration.ApplicationInsightsInstrumentationKey);

        RefreshLoggingLevel();
    }

    public void RefreshLoggingLevel()
    {
#if DEBUG
        _levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
#else
        _levelSwitch.MinimumLevel = _preferencesService.IsLoggingEnabled ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Fatal;
#endif
    }

    public void SetupLogger(string fullLogFilePath)
    {
        var insightsTelemetryConverter = new WinoTelemetryConverter(_preferencesService.DiagnosticId);

        Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                    .WriteTo.Debug()
                    .WriteTo.ApplicationInsights(_telemetryConfiguration, insightsTelemetryConverter, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error)
                    .Enrich.FromLogContext()
                    .Enrich.WithExceptionDetails()
                    .CreateLogger();
    }
}
