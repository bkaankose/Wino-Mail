using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoTelemetryService
{
    void TrackEvent(
        string eventName,
        IReadOnlyDictionary<string, string> properties = null,
        WinoTelemetryLevel level = WinoTelemetryLevel.Info);
}
