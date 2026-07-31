using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoTelemetryService
{
    void TrackEvent(WinoTelemetryEvent telemetryEvent);

    [System.Obsolete("Use the typed WinoTelemetryEvent overload.")]
    void TrackEvent(
        string eventName,
        IReadOnlyDictionary<string, string> properties = null,
        WinoTelemetryLevel level = WinoTelemetryLevel.Info);
}
