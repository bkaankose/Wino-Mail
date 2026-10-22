using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.CardDav;
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
    private const string AppModeTag = "app_mode";
    private const string EventKindTag = "event_kind";
    private const string UserInitiatedTag = "user_initiated";
    private const string DiagnosticLogsUploadOperation = "DiagnosticLogsUpload";
    private const int MaximumExceptionChainDepth = 16;

    // Provider response exceptions that report a server or account condition, matched with base types.
    private static readonly string[] ExpectedConditionTypeNames =
    [
        "Microsoft.Identity.Client.MsalServiceException",
        "Google.GoogleApiException",
        "Google.Apis.Auth.OAuth2.Responses.TokenResponseException",
        "Microsoft.Kiota.Abstractions.ApiException"
    ];

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
            // Native crash handler and offline envelope cache must live under Wino's data folder.
            // Without this the native SDK defaults to '<working directory>\.sentry-native',
            // which litters folders the app was launched from (file and protocol activations).
            options.CacheDirectoryPath = _applicationConfiguration.ApplicationDataFolderPath;
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

            // Set user context, filter out expected conditions and scrub encoded secrets.
            options.SetBeforeSend((sentryEvent, hint) =>
            {
                var currentContext = _telemetryContextProvider.GetCurrent();
                var isUserInitiated = sentryEvent.Tags.TryGetValue(UserInitiatedTag, out var userInitiated)
                    && string.Equals(userInitiated, bool.TrueString, StringComparison.OrdinalIgnoreCase);

                if (!currentContext.IsTelemetryEnabled && !isUserInitiated)
                    return null;

                if (!isUserInitiated && ShouldDropEvent(sentryEvent))
                    return null;

                WinoSentryEventScrubber.Scrub(sentryEvent);
                ApplyDiagnosticId(sentryEvent, currentContext.DiagnosticId);
                ApplyAppMetadata(sentryEvent);
                sentryEvent.SetTag(AppModeTag, currentContext.AppMode);
                return sentryEvent;
            });

            options.SetBeforeBreadcrumb((breadcrumb, hint) => WinoSentryEventScrubber.Scrub(breadcrumb));
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

    public void LogDraftUpdateFailure(System.Guid accountId, System.Guid messageId)
        => Log.Warning("Draft update failed for account {AccountId}, message {MessageId}.", accountId, messageId);

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

    internal static bool ShouldDropEvent(SentryEvent sentryEvent)
    {
        var exception = sentryEvent.Exception;

        if (exception is AttachmentPolicyCancellationException)
            return true;

        // Canceled account setups and authentication failures are expected user flows, even when wrapped.
        if (AnyInChain(exception, static candidate => candidate is AccountSetupCanceledException or AuthenticationException))
            return true;

        // Crashes are always reported.
        if (IsUnhandled(sentryEvent))
            return false;

        // Server, network and credential conditions need no code change, so they only cost quota.
        if (AnyInChain(exception, IsExpectedCondition))
            return true;

        var logger = sentryEvent.Logger ?? string.Empty;
        return logger.Contains("Synchronizer", StringComparison.Ordinal) ||
               logger.Contains("ImapClientPool", StringComparison.Ordinal) ||
               logger.Contains("GraphRateLimitHandler", StringComparison.Ordinal);
    }

    private static bool IsUnhandled(SentryEvent sentryEvent)
        => sentryEvent.Level == SentryLevel.Fatal
           || sentryEvent.SentryExceptions?.Any(exception => exception.Mechanism?.Handled == false) == true;

    private static bool IsExpectedCondition(Exception exception)
    {
        if (exception is HttpRequestException
            or SocketException
            or WebException
            or TimeoutException
            or OperationCanceledException
            or System.Security.Authentication.AuthenticationException
            or SynchronizerException
            or ImapValidationException
            or ImapTestSSLCertificateException
            or ImapClientPoolException
            or DavRequestException
            or GoogleAuthenticationException)
        {
            return true;
        }

        // Transport failures surface as IOException; missing local files are still reported.
        if (exception is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException))
            return true;

        // Wino.Services does not reference the provider SDKs, so their exceptions are matched by name.
        for (var type = exception.GetType(); type != null; type = type.BaseType)
        {
            var typeName = type.FullName ?? string.Empty;

            if (typeName.StartsWith("MailKit.", StringComparison.Ordinal) ||
                typeName.StartsWith("Microsoft.Graph.", StringComparison.Ordinal) ||
                ExpectedConditionTypeNames.Contains(typeName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyInChain(Exception? exception, Func<Exception, bool> predicate, int depth = 0)
    {
        if (exception == null || depth > MaximumExceptionChainDepth)
            return false;

        if (predicate(exception))
            return true;

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                if (AnyInChain(innerException, predicate, depth + 1))
                    return true;
            }

            return false;
        }

        return AnyInChain(exception.InnerException, predicate, depth + 1);
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
