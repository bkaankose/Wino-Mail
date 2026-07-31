using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Services;

public class WinoLogger : IWinoLogger
{
    private const string SentryDiagnosticIdTag = "diagnostic_id";
    private const string DiagnosticIdLogProperty = "DiagnosticId";
    private const string AppVersionTag = "app_version";
    private const string BuildConfigurationTag = "build_configuration";
    private const string PackageNameTag = "package_name";
    private const string DistTag = "dist";
    private const string ErrorOriginTag = "error_origin";
    private const string AppModeTag = "app_mode";
    private const string EventKindTag = "event_kind";
    private const string UserInitiatedTag = "user_initiated";
    private const string AccountSetupErrorOrigin = "AccountSetup";
    private const string DiagnosticLogsUploadOperation = "DiagnosticLogsUpload";

    private readonly LoggingLevelSwitch _levelSwitch = new LoggingLevelSwitch();
    private readonly IPreferencesService _preferencesService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IAppMetadataService _appMetadataService;
    private readonly IWinoTelemetryContextProvider _telemetryContextProvider;

    public WinoLogger(
        IPreferencesService preferencesService,
        IApplicationConfiguration applicationConfiguration,
        IAppMetadataService appMetadataService,
        IWinoTelemetryContextProvider telemetryContextProvider)
    {
        _preferencesService = preferencesService;
        _applicationConfiguration = applicationConfiguration;
        _appMetadataService = appMetadataService;
        _telemetryContextProvider = telemetryContextProvider;

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
        var telemetryContext = _telemetryContextProvider.GetCurrent();
        var diagnosticId = telemetryContext.DiagnosticId;

        // Initialize Sentry
        SentrySdk.Init(options =>
        {
            options.Dsn = _applicationConfiguration.SentryDNS;
            options.Environment = _appMetadataService.SentryEnvironment;
            options.Release = _appMetadataService.SentryRelease;
            options.DefaultTags[SentryDiagnosticIdTag] = diagnosticId;
            options.DefaultTags[AppVersionTag] = _appMetadataService.AppVersion;
            options.DefaultTags[BuildConfigurationTag] = _appMetadataService.BuildConfiguration;
            options.DefaultTags[PackageNameTag] = _appMetadataService.PackageName;
            options.DefaultTags[DistTag] = _appMetadataService.SentryDist;
            options.DefaultTags[AppModeTag] = telemetryContext.AppMode;
            options.SendDefaultPii = false;
#if DEBUG
            options.Debug = false;
#else
            options.Debug = false;
#endif
            options.AutoSessionTracking = true;

            // Set user context and filter out known exceptions.
            options.SetBeforeSend((sentryEvent, hint) =>
            {
                var currentContext = _telemetryContextProvider.GetCurrent();
                var isUserInitiated = sentryEvent.Tags.TryGetValue(UserInitiatedTag, out var userInitiated)
                    && string.Equals(userInitiated, bool.TrueString, StringComparison.OrdinalIgnoreCase);

                if (!currentContext.IsTelemetryEnabled && !isUserInitiated)
                    return null;

                // Don't send synchronization failure exceptions to Sentry.
                var isAccountSetupError = sentryEvent.Tags.TryGetValue(ErrorOriginTag, out var errorOrigin)
                    && string.Equals(errorOrigin, AccountSetupErrorOrigin, System.StringComparison.Ordinal);
                var isStructuredSyncFailure = sentryEvent.Tags.TryGetValue(EventKindTag, out var eventKind)
                    && string.Equals(eventKind, "sync_failure", StringComparison.Ordinal);

                if (!isStructuredSyncFailure &&
                    ShouldDropHandledSynchronizationEvent(sentryEvent, isAccountSetupError))
                    return null;

                ApplyDiagnosticId(sentryEvent, currentContext.DiagnosticId);
                ApplyAppMetadata(sentryEvent);
                sentryEvent.SetTag(AppModeTag, currentContext.AppMode);
                return sentryEvent;
            });
        });

        ApplyDiagnosticIdToScope(diagnosticId);
        ApplyAppMetadataToScope();
        RegisterPreferenceChangedHandler();

        Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                    // Sentry breadcrumbs must go through the explicit sanitized telemetry APIs.
                    // Local structured logs can contain mailbox or filesystem identifiers.
                    .WriteTo.Sentry(minimumBreadcrumbLevel: Serilog.Events.LogEventLevel.Fatal,
                                   minimumEventLevel: Serilog.Events.LogEventLevel.Fatal)
                    .WriteTo.Debug()
                    .Enrich.FromLogContext()
                    .Enrich.With(new DiagnosticIdEnricher(_preferencesService))
                    .CreateLogger();
    }

    public void TrackEvent(string eventName, Dictionary<string, string> properties = null)
    {
        var telemetryContext = _telemetryContextProvider.GetCurrent();
        if (!telemetryContext.IsTelemetryEnabled)
            return;

        var safeProperties = WinoTelemetrySanitizer.CreateSafeProperties(properties);
        SentrySdk.AddBreadcrumb(eventName, data: safeProperties);

        SentrySdk.ConfigureScope(scope =>
        {
            ApplyDiagnosticId(scope, telemetryContext.DiagnosticId);
            ApplyAppMetadata(scope);
            scope.SetTag(AppModeTag, telemetryContext.AppMode);

            foreach (var prop in safeProperties)
            {
                if (WinoTelemetrySanitizer.IsSearchableTag(prop.Key))
                    scope.SetTag(prop.Key, prop.Value);
            }
        });
    }

    public void CaptureException(Exception exception, string operationName = null, Dictionary<string, string> properties = null)
    {
        if (exception == null) return;

        var telemetryContext = _telemetryContextProvider.GetCurrent();
        if (!telemetryContext.IsTelemetryEnabled)
            return;

        var safeProperties = WinoTelemetrySanitizer.CreateSafeProperties(properties);

        SentrySdk.CaptureException(exception, scope =>
        {
            ApplyDiagnosticId(scope, telemetryContext.DiagnosticId);
            ApplyAppMetadata(scope);
            scope.SetTag(AppModeTag, telemetryContext.AppMode);
            scope.SetTag(EventKindTag, "exception");

            if (!string.IsNullOrWhiteSpace(operationName))
            {
                scope.SetTag("operation", operationName);
                scope.SetExtra("Operation", operationName);
            }

            foreach (var property in safeProperties)
            {
                if (WinoTelemetrySanitizer.IsSearchableTag(property.Key))
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
        ApplyAppMetadata(sentryEvent);
        sentryEvent.SetTag("operation", DiagnosticLogsUploadOperation);
        sentryEvent.SetTag(EventKindTag, "diagnostic_upload");
        sentryEvent.SetTag(UserInitiatedTag, bool.TrueString);

        var hint = new SentryHint();
        hint.AddAttachment(logArchivePath, AttachmentType.Default, "application/zip");

        SentrySdk.CaptureEvent(sentryEvent, hint, scope =>
        {
            ApplyDiagnosticId(scope, diagnosticId);
            ApplyAppMetadata(scope);
            scope.SetTag("operation", DiagnosticLogsUploadOperation);
            scope.SetTag(EventKindTag, "diagnostic_upload");
            scope.SetTag(UserInitiatedTag, bool.TrueString);
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

    private void ApplyAppMetadataToScope()
        => SentrySdk.ConfigureScope(ApplyAppMetadata);

    private void ApplyAppMetadata(Scope scope)
    {
        WinoTelemetryContextSnapshot telemetryContext = _telemetryContextProvider.GetCurrent();
        scope.SetTag(AppVersionTag, _appMetadataService.AppVersion);
        scope.SetTag(BuildConfigurationTag, _appMetadataService.BuildConfiguration);
        scope.SetTag(PackageNameTag, _appMetadataService.PackageName);
        scope.SetTag(DistTag, _appMetadataService.SentryDist);
        scope.SetTag(AppModeTag, telemetryContext.AppMode);
        scope.SetExtra("SentryEnvironment", _appMetadataService.SentryEnvironment);
        scope.SetExtra("SentryRelease", _appMetadataService.SentryRelease);
        scope.SetExtra("SentryDist", _appMetadataService.SentryDist);
    }

    private void ApplyAppMetadata(SentryEvent sentryEvent)
    {
        WinoTelemetryContextSnapshot telemetryContext = _telemetryContextProvider.GetCurrent();
        sentryEvent.SetTag(AppVersionTag, _appMetadataService.AppVersion);
        sentryEvent.SetTag(BuildConfigurationTag, _appMetadataService.BuildConfiguration);
        sentryEvent.SetTag(PackageNameTag, _appMetadataService.PackageName);
        sentryEvent.SetTag(DistTag, _appMetadataService.SentryDist);
        sentryEvent.SetTag(AppModeTag, telemetryContext.AppMode);
        sentryEvent.SetExtra("SentryEnvironment", _appMetadataService.SentryEnvironment);
        sentryEvent.SetExtra("SentryRelease", _appMetadataService.SentryRelease);
        sentryEvent.SetExtra("SentryDist", _appMetadataService.SentryDist);
    }

    private static bool ShouldDropHandledSynchronizationEvent(SentryEvent sentryEvent, bool isAccountSetupError)
    {
        if (isAccountSetupError || sentryEvent.Level == SentryLevel.Fatal)
            return false;

        if (sentryEvent.Exception is SynchronizerException)
            return true;

        var logger = sentryEvent.Logger ?? string.Empty;
        if (logger.Contains("Synchronizer", StringComparison.Ordinal) ||
            logger.Contains("ImapClientPool", StringComparison.Ordinal) ||
            logger.Contains("GraphRateLimitHandler", StringComparison.Ordinal))
        {
            return true;
        }

        var exceptionType = sentryEvent.Exception?.GetType().FullName ?? string.Empty;
        return exceptionType.Contains("MailKit", StringComparison.Ordinal) ||
               exceptionType.Contains("Google.GoogleApiException", StringComparison.Ordinal) ||
               exceptionType.Contains("Microsoft.Graph", StringComparison.Ordinal);
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
