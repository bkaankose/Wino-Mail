using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services
{
    public class LogInitializer : ILogInitializer
    {
        private readonly LoggingLevelSwitch _levelSwitch = new LoggingLevelSwitch();
        private readonly IPreferencesService _preferencesService;

        public LogInitializer(IPreferencesService preferencesService)
        {
            _preferencesService = preferencesService;

            RefreshLoggingLevel();
        }

        public void RefreshLoggingLevel()
        {
            _levelSwitch.MinimumLevel = _preferencesService.IsLoggingEnabled ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Fatal;
        }

        public void SetupLogger(string fullLogFilePath)
        {
            Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.ControlledBy(_levelSwitch)
                        .WriteTo.File(fullLogFilePath, retainedFileCountLimit: 3, rollOnFileSizeLimit: true, rollingInterval: RollingInterval.Day)
                        .WriteTo.Debug()
                        .Enrich.FromLogContext()
                        .Enrich.WithExceptionDetails()
                        .CreateLogger();
        }
    }
}
