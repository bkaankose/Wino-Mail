using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Intelligence.ConsoleApp;

internal static class ConsoleOutput
{
    public static void Header(string text) => WriteLine(text, ConsoleColor.Cyan);
    public static void Success(string text) => WriteLine(text, ConsoleColor.Green);
    public static void Warning(string text) => WriteLine(text, ConsoleColor.Yellow);
    public static void Error(string? text) => WriteLine(text ?? string.Empty, ConsoleColor.Red, useErrorStream: true);
    public static void Muted(string text) => WriteLine(text, ConsoleColor.DarkGray);

    public static void Prompt(string text)
    {
        WithColor(ConsoleColor.Yellow, () => System.Console.Write(text));
    }

    public static void Status(string text, SemanticIndexJobStatus status)
    {
        var color = status switch
        {
            SemanticIndexJobStatus.Completed => ConsoleColor.Green,
            SemanticIndexJobStatus.Failed or SemanticIndexJobStatus.Cancelled => ConsoleColor.Red,
            SemanticIndexJobStatus.PausedForQuota => ConsoleColor.Yellow,
            SemanticIndexJobStatus.Indexing or SemanticIndexJobStatus.GeneratingInsights => ConsoleColor.Cyan,
            _ => ConsoleColor.Gray,
        };
        WriteLine(text, color);
    }

    private static void WriteLine(string text, ConsoleColor color, bool useErrorStream = false)
    {
        WithColor(color, () =>
        {
            if (useErrorStream)
                System.Console.Error.WriteLine(text);
            else
                System.Console.WriteLine(text);
        });
    }

    private static void WithColor(ConsoleColor color, Action write)
    {
        if (System.Console.IsOutputRedirected)
        {
            write();
            return;
        }

        var previous = System.Console.ForegroundColor;
        try
        {
            System.Console.ForegroundColor = color;
            write();
        }
        finally
        {
            System.Console.ForegroundColor = previous;
        }
    }
}
