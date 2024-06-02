using System.Diagnostics;

namespace Wino.Mail.ConsoleTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var outlookTest = new OutlookTest();

            outlookTest.InitializeTestAsync().Wait();
            outlookTest.StartAsync().Wait();

            stopwatch.Stop();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Sync finished in {stopwatch.Elapsed.TotalMinutes} minutes.");
        }
    }
}
