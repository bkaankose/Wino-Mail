using System.Text.Json;

namespace Wino.NotificationHost.Contracts;

/// <summary>The single file the main application writes and the widget host reads.</summary>
public static class WidgetSnapshotStore
{
    public const string DirectoryName = "Widgets";
    public const string FileName = "glance.json";
    private const int WriteAttempts = 3;

    public static string GetDirectory(string localCachePath) => Path.Combine(localCachePath, DirectoryName);

    public static string GetPath(string localCachePath) => Path.Combine(GetDirectory(localCachePath), FileName);

    public static string Serialize(WidgetSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, WidgetSnapshotJsonContext.Default.WidgetSnapshot);

    /// <summary>Replaces the file in one move, so the host never reads a half-written snapshot.</summary>
    public static void Write(string localCachePath, string json)
    {
        var directory = GetDirectory(localCachePath);
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, GetPath(localCachePath), overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < WriteAttempts)
                {
                    Thread.Sleep(100 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < WriteAttempts)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    public static WidgetSnapshot? TryRead(string localCachePath)
    {
        try
        {
            var path = GetPath(localCachePath);
            if (!File.Exists(path))
                return null;

            // Delete sharing lets the main application replace the file while the host reads it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, WidgetSnapshotJsonContext.Default.WidgetSnapshot);
        }
        catch
        {
            return null;
        }
    }
}
