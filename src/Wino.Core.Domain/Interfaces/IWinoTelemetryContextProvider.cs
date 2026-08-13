using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoTelemetryContextProvider
{
    WinoTelemetryContextSnapshot GetCurrent();
}
