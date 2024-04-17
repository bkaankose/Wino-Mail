namespace Wino.Core.Domain.Interfaces
{
    public interface ILogInitializer
    {
        void SetupLogger(string logFolderPath);

        void RefreshLoggingLevel();
    }
}
