using System.Collections.Generic;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class WinoLogger : IWinoLogger
{
    private readonly LoggingLevelSwitch _levelSwitch = new LoggingLevelSwitch();
    private readonly IPreferencesService _preferencesService;
    private readonly IApplicationConfiguration _applicationConfiguration;

    public WinoLogger(IPreferencesService preferencesService, IApplicationConfiguration applicationConfiguration)
    {
        _preferencesService = preferencesService;
        _applicationConfiguration = applicationConfiguration;

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
        // Make sure to set the diagnostic id for the telemetry converter.
        // This call seems weird, but it is necessary to make sure the diagnostic id is set.
        _preferencesService.DiagnosticId = _preferencesService.DiagnosticId;

        // Initialize Sentry
        SentrySdk.Init(options =>
        {
            options.Dsn = _applicationConfiguration.SentryDNS;
#if DEBUG
            options.Debug = false;
#else
            options.Debug = true;
#endif
            options.AutoSessionTracking = true;

            // Set user context
            options.SetBeforeSend((sentryEvent, hint) =>
            {
                sentryEvent.User = new SentryUser
                {
                    Id = _preferencesService.DiagnosticId
                };
                return sentryEvent;
            });
        });

        Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                    .WriteTo.Sentry(minimumBreadcrumbLevel: Serilog.Events.LogEventLevel.Information,
                                   minimumEventLevel: Serilog.Events.LogEventLevel.Error)
                    .WriteTo.Debug()
                    .Enrich.FromLogContext()
                    .Enrich.WithExceptionDetails()
                    .CreateLogger();
    }

    public void TrackEvent(string eventName, Dictionary<string, string> properties = null)
    {
        SentrySdk.AddBreadcrumb(eventName, data: properties);

        SentrySdk.ConfigureScope(scope =>
        {
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    scope.SetTag(prop.Key, prop.Value);
                }
            }
        });
    }
}
