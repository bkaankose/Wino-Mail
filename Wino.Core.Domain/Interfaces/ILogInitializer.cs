namespace Wino.Core.Domain.Interfaces
{
    public interface ILogInitializer
    {
        void SetupLogger(string fullLogFilePath);

        void RefreshLoggingLevel();
    }
}
