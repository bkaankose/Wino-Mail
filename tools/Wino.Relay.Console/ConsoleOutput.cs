using Serilog.Core;
using Serilog.Events;

namespace Wino.Relay.ConsoleApp;

internal static class ConsoleOutput
{
    private static readonly Lock Gate = new();

    public static void Header(string text) => WriteLine(text, ConsoleColor.Cyan);
    public static void Event(string text) => WriteLine(text, ConsoleColor.Green);
    public static void Warning(string text) => WriteLine(text, ConsoleColor.Yellow);
    public static void Error(string text) => WriteLine(text, ConsoleColor.Red);
    public static void Muted(string text) => WriteLine(text, ConsoleColor.DarkGray);
    public static void Info(string text) => WriteLine(text, ConsoleColor.Gray);
    public static void KeyValue(string key, object? value) => Info($"  {key,-22} {value}");

    public static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    public static string? Ask(string prompt)
    {
        lock (Gate)
        {
            WithColor(ConsoleColor.Yellow, () => Console.Write(prompt));
        }

        return Console.ReadLine()?.Trim();
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        lock (Gate)
        {
            WithColor(color, () => Console.WriteLine(text));
            Console.Out.Flush();
        }
    }

    private static void WithColor(ConsoleColor color, Action write)
    {
        if (Console.IsOutputRedirected)
        {
            write();
            return;
        }

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            write();
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    /// <summary>The services' Serilog lines, dimmed so the relay events stand out.</summary>
    public sealed class SerilogSink(LogEventLevel minimum) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < minimum) return;
            var text = $"[{logEvent.Timestamp:HH:mm:ss.fff}] {logEvent.Level.ToString()[..3].ToUpperInvariant()} {logEvent.RenderMessage()}";
            if (logEvent.Exception is not null) text += $" ({logEvent.Exception.GetType().Name}: {logEvent.Exception.Message})";
            if (logEvent.Level >= LogEventLevel.Warning) Warning(text); else Muted(text);
        }
    }
}
