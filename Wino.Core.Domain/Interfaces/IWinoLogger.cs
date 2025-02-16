using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoLogger
{
    void SetupLogger(string fullLogFilePath);
    void RefreshLoggingLevel();
    void TrackEvent(string eventName, Dictionary<string, string> properties = null);
}
