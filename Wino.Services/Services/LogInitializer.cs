using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Wino.Domain.Interfaces;

namespace Wino.Services.Services
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

        public void SetupLogger(string logFolderPath)
        {
            string logFilePath = Path.Combine(logFolderPath, Domain.Constants.WinoLogFileName);

            Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.ControlledBy(_levelSwitch)
                        .WriteTo.File(logFilePath)
                        .WriteTo.Debug()
                        .Enrich.FromLogContext()
                        .Enrich.WithExceptionDetails()
                        .CreateLogger();
        }
    }
}
