using System;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Interfaces;

public interface IMailCalLogger
{
    void LogDraftUpdateFailure(Guid accountId, Guid messageId);
    void SetupLogger(string fullLogFilePath);
    void RefreshLoggingLevel();
    void TrackEvent(string eventName, Dictionary<string, string> properties = null);
    void CaptureException(Exception exception, string operationName = null, Dictionary<string, string> properties = null);
}
