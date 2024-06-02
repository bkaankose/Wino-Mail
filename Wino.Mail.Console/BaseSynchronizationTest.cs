using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ConsoleTest
{
    public class BaseSynchronizationTest : ISynchronizationProgress
    {
        public void AccountProgressUpdated(Guid accountId, int progress) => LogMessage($"Account {accountId} progress: {progress}%");

        #region Logging

        public void LogMessage(string message)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
        }

        public void LogError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
        }

        public void LogWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
        }

        public void LogSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
        }

        #endregion
    }
}
