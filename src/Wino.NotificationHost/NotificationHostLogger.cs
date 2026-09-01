using Windows.Storage;

namespace Wino.NotificationHost;

internal static class NotificationHostLogger
{
    private static readonly object SyncRoot = new();

    public static void Write(string operation, Guid? requestId = null, Exception? exception = null)
    {
        try
        {
            var logDirectory = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "NotificationHost", "Logs");
            Directory.CreateDirectory(logDirectory);
            var line = $"{DateTimeOffset.UtcNow:O}\t{CurrentAppIdentity.GetAppUserModelId()}\t{operation}\t{requestId?.ToString("N") ?? "-"}\t{exception?.GetType().Name ?? "-"}\t0x{exception?.HResult:X8}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(Path.Combine(logDirectory, "NotificationHost.log"), line);
            }
        }
        catch
        {
        }
    }
}
