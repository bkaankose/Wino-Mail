using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class WinoLogger : IWinoLogger
{
    private const string SentryDiagnosticIdTag = "diagnostic_id";
    private const string DiagnosticIdLogProperty = "DiagnosticId";
    private const string ErrorOriginTag = "error_origin";
    private const string AccountSetupErrorOrigin = "AccountSetup";
    private const string DiagnosticLogsUploadOperation = "DiagnosticLogsUpload";

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
        var diagnosticId = _preferencesService.DiagnosticId;

        // Initialize Sentry
        SentrySdk.Init(options =>
        {
            options.Dsn = _applicationConfiguration.SentryDNS;
            options.DefaultTags[SentryDiagnosticIdTag] = diagnosticId;
#if DEBUG
            options.Debug = false;
#else
            options.Debug = true;
#endif
            options.AutoSessionTracking = true;

            // Set user context and filter out known exceptions.
            options.SetBeforeSend((sentryEvent, hint) =>
            {
                // Don't send synchronization failure exceptions to Sentry.
                var isAccountSetupError = sentryEvent.Tags.TryGetValue(ErrorOriginTag, out var errorOrigin)
                    && string.Equals(errorOrigin, AccountSetupErrorOrigin, System.StringComparison.Ordinal);

                if (sentryEvent.Exception is SynchronizerException && !isAccountSetupError)
                    return null;

                ApplyDiagnosticId(sentryEvent, _preferencesService.DiagnosticId);
                return sentryEvent;
            });
        });

        ApplyDiagnosticIdToScope(diagnosticId);
        RegisterPreferenceChangedHandler();

        Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                    .WriteTo.Sentry(minimumBreadcrumbLevel: Serilog.Events.LogEventLevel.Information,
                                   minimumEventLevel: Serilog.Events.LogEventLevel.Error)
                    .WriteTo.Debug()
                    .Enrich.FromLogContext()
                    .Enrich.With(new DiagnosticIdEnricher(_preferencesService))
                    .Enrich.WithExceptionDetails()
                    .CreateLogger();
    }

    public void TrackEvent(string eventName, Dictionary<string, string> properties = null)
    {
        SentrySdk.AddBreadcrumb(eventName, data: properties);

        SentrySdk.ConfigureScope(scope =>
        {
            ApplyDiagnosticId(scope, _preferencesService.DiagnosticId);

            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    scope.SetTag(prop.Key, prop.Value);
                }
            }
        });
    }

    public void CaptureException(Exception exception, string operationName = null, Dictionary<string, string> properties = null)
    {
        if (exception == null) return;

        SentrySdk.CaptureException(exception, scope =>
        {
            ApplyDiagnosticId(scope, _preferencesService.DiagnosticId);

            if (!string.IsNullOrWhiteSpace(operationName))
            {
                scope.SetTag("operation", operationName);
                scope.SetExtra("Operation", operationName);
            }

            if (properties == null) return;

            foreach (var property in properties)
            {
                if (string.IsNullOrWhiteSpace(property.Key) || property.Value == null) continue;

                scope.SetTag(property.Key, property.Value);
                scope.SetExtra(property.Key, property.Value);
            }
        });
    }

    public async Task UploadDiagnosticLogsAsync(string logArchivePath, string diagnosticId)
    {
        if (string.IsNullOrWhiteSpace(logArchivePath)) return;

        var sentryEvent = new SentryEvent
        {
            Level = SentryLevel.Info,
            Message = $"Diagnostic logs uploaded: {diagnosticId}"
        };

        ApplyDiagnosticId(sentryEvent, diagnosticId);
        sentryEvent.SetTag("operation", DiagnosticLogsUploadOperation);

        var hint = new SentryHint();
        hint.AddAttachment(logArchivePath, AttachmentType.Default, "application/zip");

        SentrySdk.CaptureEvent(sentryEvent, hint, scope =>
        {
            ApplyDiagnosticId(scope, diagnosticId);
            scope.SetTag("operation", DiagnosticLogsUploadOperation);
        });

        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
    }

    private void RegisterPreferenceChangedHandler()
    {
        _preferencesService.PropertyChanged -= PreferencesServicePropertyChanged;
        _preferencesService.PropertyChanged += PreferencesServicePropertyChanged;
    }

    private void PreferencesServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPreferencesService.DiagnosticId))
        {
            ApplyDiagnosticIdToScope(_preferencesService.DiagnosticId);
        }
    }

    private static void ApplyDiagnosticIdToScope(string diagnosticId)
        => SentrySdk.ConfigureScope(scope => ApplyDiagnosticId(scope, diagnosticId));

    private static void ApplyDiagnosticId(Scope scope, string diagnosticId)
    {
        scope.User = new SentryUser
        {
            Id = diagnosticId
        };
        scope.SetTag(SentryDiagnosticIdTag, diagnosticId);
        scope.SetExtra(DiagnosticIdLogProperty, diagnosticId);
    }

    private static void ApplyDiagnosticId(SentryEvent sentryEvent, string diagnosticId)
    {
        sentryEvent.User ??= new SentryUser();
        sentryEvent.User.Id = diagnosticId;
        sentryEvent.SetTag(SentryDiagnosticIdTag, diagnosticId);
        sentryEvent.SetExtra(DiagnosticIdLogProperty, diagnosticId);
    }

    private sealed class DiagnosticIdEnricher(IPreferencesService preferencesService) : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var diagnosticId = preferencesService.DiagnosticId;

            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(DiagnosticIdLogProperty, diagnosticId));
        }
    }
}
