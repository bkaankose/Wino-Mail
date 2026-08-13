using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Telemetry;

public sealed record WinoTelemetryEvent
{
    public required string Name { get; init; }

    public WinoTelemetryLevel Level { get; init; } = WinoTelemetryLevel.Info;

    public Exception? Exception { get; init; }

    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    public IReadOnlyDictionary<string, string>? Context { get; init; }

    public IReadOnlyList<string>? Fingerprint { get; init; }

    public bool CaptureAsEvent { get; init; } = true;

    /// <summary>
    /// Local-only key used to suppress repeated telemetry. It is never serialized.
    /// </summary>
    public string? DeduplicationKey { get; init; }

    public TimeSpan? DeduplicationWindow { get; init; }
}
