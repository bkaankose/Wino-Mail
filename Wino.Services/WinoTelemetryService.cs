using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sentry;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed partial class WinoTelemetryService : IWinoTelemetryService
{
    private const int MaxPropertyValueLength = 200;

    private readonly IAppMetadataService _appMetadataService;
    private readonly ILogger<WinoTelemetryService> _logger;

    public WinoTelemetryService(
        IAppMetadataService appMetadataService,
        ILogger<WinoTelemetryService> logger)
    {
        _appMetadataService = appMetadataService;
        _logger = logger;
    }

    public void TrackEvent(
        string eventName,
        IReadOnlyDictionary<string, string> properties = null,
        WinoTelemetryLevel level = WinoTelemetryLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return;

        var safeProperties = CreateSafeProperties(properties);
        safeProperties["event_name"] = eventName.Trim();
        safeProperties["app_version"] = _appMetadataService.AppVersion;
        safeProperties["build_configuration"] = _appMetadataService.BuildConfiguration;
        safeProperties["environment"] = _appMetadataService.SentryEnvironment;
        safeProperties["release"] = _appMetadataService.SentryRelease;
        safeProperties["dist"] = _appMetadataService.SentryDist;
        safeProperties["package_name"] = _appMetadataService.PackageName;

        WinoTelemetryLog.TelemetryEventTracked(_logger, eventName, level.ToString());

        SentrySdk.AddBreadcrumb(eventName, category: "telemetry", data: safeProperties);

        var sentryEvent = new SentryEvent
        {
            Level = ToSentryLevel(level),
            Logger = "Wino.Telemetry",
            Message = eventName
        };

        sentryEvent.SetTag("telemetry_event", eventName);
        sentryEvent.SetTag("app_version", _appMetadataService.AppVersion);
        sentryEvent.SetTag("build_configuration", _appMetadataService.BuildConfiguration);
        sentryEvent.SetTag("environment", _appMetadataService.SentryEnvironment);
        sentryEvent.SetTag("release", _appMetadataService.SentryRelease);
        sentryEvent.SetTag("dist", _appMetadataService.SentryDist);
        sentryEvent.SetTag("package_name", _appMetadataService.PackageName);

        foreach (var property in safeProperties)
        {
            sentryEvent.SetTag(property.Key, property.Value);
            sentryEvent.SetExtra(property.Key, property.Value);
        }

        SentrySdk.CaptureEvent(sentryEvent);
    }

    private static Dictionary<string, string> CreateSafeProperties(IReadOnlyDictionary<string, string> properties)
    {
        var safeProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties == null)
            return safeProperties;

        foreach (var property in properties.Where(property => !string.IsNullOrWhiteSpace(property.Key)))
        {
            if (IsForbiddenPropertyKey(property.Key) || property.Value == null)
                continue;

            safeProperties[property.Key.Trim()] = NormalizeValue(property.Value);
        }

        return safeProperties;
    }

    private static bool IsForbiddenPropertyKey(string key)
    {
        var normalizedKey = key.Trim().ToLowerInvariant();

        return normalizedKey.Contains("password", StringComparison.Ordinal)
               || normalizedKey.Contains("token", StringComparison.Ordinal)
               || normalizedKey.Contains("secret", StringComparison.Ordinal)
               || normalizedKey.Contains("username", StringComparison.Ordinal)
               || normalizedKey.Contains("email", StringComparison.Ordinal)
               || normalizedKey.Contains("local_part", StringComparison.Ordinal)
               || normalizedKey.Contains("subject", StringComparison.Ordinal)
               || normalizedKey.Contains("content", StringComparison.Ordinal)
               || normalizedKey.Contains("body", StringComparison.Ordinal)
               || normalizedKey.Contains("query", StringComparison.Ordinal)
               || normalizedKey.Contains("path", StringComparison.Ordinal);
    }

    private static string NormalizeValue(string value)
    {
        var normalizedValue = value.Trim();
        return normalizedValue.Length <= MaxPropertyValueLength
            ? normalizedValue
            : normalizedValue[..MaxPropertyValueLength];
    }

    private static SentryLevel ToSentryLevel(WinoTelemetryLevel level)
        => level switch
        {
            WinoTelemetryLevel.Debug => SentryLevel.Debug,
            WinoTelemetryLevel.Warning => SentryLevel.Warning,
            WinoTelemetryLevel.Error => SentryLevel.Error,
            WinoTelemetryLevel.Fatal => SentryLevel.Fatal,
            _ => SentryLevel.Info
        };
}

public static partial class WinoTelemetryLog
{
    [LoggerMessage(EventId = 10000, Level = LogLevel.Information, Message = "Tracked telemetry event {EventName} with level {TelemetryLevel}.")]
    public static partial void TelemetryEventTracked(ILogger logger, string eventName, string telemetryLevel);
}
