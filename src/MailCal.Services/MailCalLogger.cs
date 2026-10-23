using System;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Services;

/// <summary>
/// Local-only logging. Logs are written to rolling files in the application data folder
/// and never leave the machine.
/// </summary>
public class MailCalLogger : IMailCalLogger
{
    private readonly LoggingLevelSwitch _levelSwitch = new LoggingLevelSwitch();
    private readonly IPreferencesService _preferencesService;

    public MailCalLogger(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

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
        Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_levelSwitch)
                    .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                    .WriteTo.Debug()
                    .Enrich.FromLogContext()
                    .CreateLogger();
    }

    public void LogDraftUpdateFailure(Guid accountId, Guid messageId)
        => Log.Warning("Draft update failed for account {AccountId}, message {MessageId}.", accountId, messageId);

    public void TrackEvent(string eventName, Dictionary<string, string> properties = null)
        => Log.Debug("Event {EventName} {@Properties}", eventName, properties);

    public void CaptureException(Exception exception, string operationName = null, Dictionary<string, string> properties = null)
    {
        if (exception == null) return;

        Log.Error(exception, "Operation {OperationName} failed {@Properties}", operationName, properties);
    }
}
