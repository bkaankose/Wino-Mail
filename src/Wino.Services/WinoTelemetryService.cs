using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sentry;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Services;

public sealed partial class WinoTelemetryService : IWinoTelemetryService
{
    private readonly IWinoTelemetryContextProvider _contextProvider;
    private readonly ILogger<WinoTelemetryService> _logger;
    private readonly IWinoTelemetrySink _sink;
    private readonly WinoTelemetryDeduplicator _deduplicator = new();

    public WinoTelemetryService(
        IWinoTelemetryContextProvider contextProvider,
        ILogger<WinoTelemetryService> logger,
        IWinoTelemetrySink sink)
    {
        _contextProvider = contextProvider;
        _logger = logger;
        _sink = sink;
    }

    [Obsolete("Use the typed WinoTelemetryEvent overload.")]
    public void TrackEvent(
        string eventName,
        IReadOnlyDictionary<string, string> properties = null,
        WinoTelemetryLevel level = WinoTelemetryLevel.Info)
        => TrackEvent(new WinoTelemetryEvent
        {
            Name = eventName,
            Level = level,
            Tags = properties
        });

    public void TrackEvent(WinoTelemetryEvent telemetryEvent)
    {
        if (telemetryEvent == null || string.IsNullOrWhiteSpace(telemetryEvent.Name))
            return;

        var telemetryContext = _contextProvider.GetCurrent();
        if (!telemetryContext.IsTelemetryEnabled)
            return;

        if (!_deduplicator.ShouldSend(
                telemetryEvent.DeduplicationKey,
                telemetryEvent.DeduplicationWindow,
                DateTimeOffset.UtcNow,
                out var suppressedRepeatCount))
        {
            return;
        }

        var eventName = telemetryEvent.Name.Trim();
        var safeTags = WinoTelemetrySanitizer.CreateSafeProperties(telemetryEvent.Tags);
        var safeContext = WinoTelemetrySanitizer.CreateSafeProperties(telemetryEvent.Context);

        safeTags["event_name"] = eventName;
        safeTags["diagnostic_id"] = telemetryContext.DiagnosticId;
        safeTags["app_mode"] = telemetryContext.AppMode;
        safeTags["app_version"] = telemetryContext.AppVersion;
        safeTags["build_configuration"] = telemetryContext.BuildConfiguration;
        safeTags["environment"] = telemetryContext.SentryEnvironment;
        safeTags["release"] = telemetryContext.SentryRelease;
        safeTags["dist"] = telemetryContext.SentryDist;
        safeTags["package_name"] = telemetryContext.PackageName;

        if (suppressedRepeatCount > 0)
        {
            safeContext["suppressed_repeat_count"] = suppressedRepeatCount.ToString();
        }

        WinoTelemetryLog.TelemetryEventTracked(_logger, eventName, telemetryEvent.Level.ToString());

        _sink.AddBreadcrumb(eventName, safeTags);

        if (!telemetryEvent.CaptureAsEvent)
        {
            return;
        }

        var sentryEvent = telemetryEvent.Exception == null
            ? new SentryEvent()
            : new SentryEvent(telemetryEvent.Exception);

        sentryEvent.Level = ToSentryLevel(telemetryEvent.Level);
        sentryEvent.Logger = "Wino.Telemetry";
        sentryEvent.Message = eventName;

        sentryEvent.User = new SentryUser { Id = telemetryContext.DiagnosticId };
        sentryEvent.SetTag("telemetry_event", eventName);

        foreach (var property in safeTags)
        {
            sentryEvent.SetTag(property.Key, property.Value);
        }

        if (safeContext.Count > 0)
        {
            sentryEvent.Contexts["diagnostics"] = safeContext;
        }

        if (telemetryEvent.Fingerprint is { Count: > 0 })
        {
            sentryEvent.SetFingerprint(telemetryEvent.Fingerprint);
        }

        _sink.CaptureEvent(sentryEvent);
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
