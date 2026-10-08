using System.Diagnostics;

namespace Wino.Intelligence.ConsoleApp.Hosting;

internal static class ConsoleOutput
{
    private static readonly Lock WriteGate = new();

    /// <summary>Set by --yes: every confirmation is answered yes.</summary>
    public static bool AssumeYes { get; set; }

    public static void Header(string text) => WriteLine(text, ConsoleColor.Cyan);
    public static void Success(string text) => WriteLine(text, ConsoleColor.Green);
    public static void Warning(string text) => WriteLine(text, ConsoleColor.Yellow);
    public static void Error(string? text) => WriteLine(text ?? string.Empty, ConsoleColor.Red);
    public static void Muted(string text) => WriteLine(text, ConsoleColor.DarkGray);
    public static void Info(string text) => WriteLine(text, ConsoleColor.Gray);

    /// <summary>A line prefixed with the time since <paramref name="clock"/> started.</summary>
    public static void Timeline(Stopwatch clock, string text, ConsoleColor color = ConsoleColor.Gray)
        => WriteLine($"[{clock.Elapsed:mm\\:ss\\.fff}] {text}", color);

    public static void KeyValue(string key, object? value, int width = 30)
        => Info($"  {key.PadRight(width)} {value}");

    public static string Elapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds < 1 ? $"{elapsed.TotalMilliseconds:0} ms"
            : elapsed.TotalMinutes < 1 ? $"{elapsed.TotalSeconds:0.00} s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";

    public static async Task<T> TimedAsync<T>(string label, Func<Task<T>> action)
    {
        var clock = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        Muted($"  {label}: {Elapsed(clock.Elapsed)}");
        return result;
    }

    public static async Task TimedAsync(string label, Func<Task> action)
    {
        var clock = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        Muted($"  {label}: {Elapsed(clock.Elapsed)}");
    }

    public static string Ask(string prompt, string? defaultValue = null)
    {
        lock (WriteGate)
        {
            WithColor(ConsoleColor.Yellow, () =>
                Console.Write(defaultValue is null ? $"{prompt}: " : $"{prompt} [{defaultValue}]: "));
        }

        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ? defaultValue ?? string.Empty : answer;
    }

    public static bool Confirm(string question)
    {
        if (AssumeYes)
        {
            Muted($"{question} (y/n): y [--yes]");
            return true;
        }

        return Ask($"{question} (y/n)", "n").StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public static int AskNumber(string prompt, int defaultValue, int min, int max)
    {
        while (true)
        {
            var text = Ask(prompt, defaultValue.ToString());
            if (int.TryParse(text, out var value) && value >= min && value <= max)
                return value;

            Warning($"Enter a number between {min} and {max}.");
        }
    }

    /// <summary>Shows a numbered list and returns the chosen index, or -1 for "back".</summary>
    public static int Choose<T>(string title, IReadOnlyList<T> items, Func<T, string> describe, int defaultIndex = 0)
    {
        Header(title);
        for (var index = 0; index < items.Count; index++)
            Info($"  {index + 1,2}. {describe(items[index])}");
        Info("   0. Back");

        var choice = AskNumber("Choose", defaultIndex + 1, 0, items.Count);
        return choice - 1;
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        lock (WriteGate)
        {
            WithColor(color, () => Console.WriteLine(text));
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
}
