using System.Diagnostics;

namespace Wino.Companion;

internal static class CompanionDiagnostics
{
    private static readonly object FileLock = new();

    public static void Write(string message, Exception? exception = null)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}";
        if (exception is not null)
        {
            entry += $"{Environment.NewLine}{exception}";
        }

        // Write to OutputDebugString before touching the file system so Visual
        // Studio still receives diagnostics if package storage is unavailable.
        Debug.WriteLine($"Companion: {entry}");

        try
        {
            var directory = Path.Combine(CompanionPaths.LocalData, "Companion");
            Directory.CreateDirectory(directory);
            lock (FileLock)
            {
                File.AppendAllText(
                    Path.Combine(directory, "companion-trace.log"),
                    entry + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never affect companion lifetime.
        }
    }
}
