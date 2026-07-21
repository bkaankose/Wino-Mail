using System.Diagnostics;
using Windows.Storage;

namespace Wino.Mail.Uwp.Services;

internal static class UwpDiagnostics
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);

    public static void Write(string message, Exception? exception = null)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}";
        if (exception is not null)
        {
            entry += $"{Environment.NewLine}{exception}";
        }

        Debug.WriteLine($"UWP: {entry}");
        _ = Task.Run(() => WriteFileAsync(entry));
    }

    private static async Task WriteFileAsync(string entry)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "uwp-trace.log");
            await File.AppendAllTextAsync(logPath, entry + Environment.NewLine).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never affect activation or application lifetime.
        }
        finally
        {
            FileGate.Release();
        }
    }
}
