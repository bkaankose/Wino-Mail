namespace Wino.NotificationHost.Contracts;

public static class NotificationHostFileStore
{
    public static Task WriteRequestAsync(
        string localCachePath,
        Guid requestId,
        NotificationHostRequest request,
        CancellationToken cancellationToken = default)
        => WriteEnvelopeAsync(
            NotificationHostPaths.GetRequestPath(localCachePath, requestId),
            NotificationHostCodec.EncodeRequest(request),
            cancellationToken);

    public static NotificationHostRequest ReadRequest(string localCachePath, Guid requestId)
        => NotificationHostCodec.DecodeRequest(File.ReadAllBytes(NotificationHostPaths.GetRequestPath(localCachePath, requestId)));

    public static Task WriteActivationAsync(
        string localCachePath,
        Guid activationId,
        NotificationHostActivation activation,
        CancellationToken cancellationToken = default)
        => WriteEnvelopeAsync(
            NotificationHostPaths.GetActivationPath(localCachePath, activationId),
            NotificationHostCodec.EncodeActivation(activation),
            cancellationToken);

    public static NotificationHostActivation ReadActivation(string localCachePath, Guid activationId)
        => NotificationHostCodec.DecodeActivation(File.ReadAllBytes(NotificationHostPaths.GetActivationPath(localCachePath, activationId)));

    public static bool TryDeleteRequest(string localCachePath, Guid requestId)
        => TryDelete(NotificationHostPaths.GetRequestPath(localCachePath, requestId));

    public static bool TryDeleteActivation(string localCachePath, Guid activationId)
        => TryDelete(NotificationHostPaths.GetActivationPath(localCachePath, activationId));

    public static int CleanupStaleFiles(string localCachePath, TimeSpan maximumAge, DateTimeOffset? nowUtc = null)
    {
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow) - maximumAge;
        return CleanupDirectory(NotificationHostPaths.GetRequestDirectory(localCachePath), cutoffUtc) +
               CleanupDirectory(NotificationHostPaths.GetActivationDirectory(localCachePath), cutoffUtc);
    }

    private static async Task WriteEnvelopeAsync(string finalPath, byte[] data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("Notification host envelope path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static int CleanupDirectory(string directory, DateTimeOffset cutoffUtc)
    {
        if (!Directory.Exists(directory))
            return 0;

        var deletedCount = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc.UtcDateTime && TryDelete(path))
                    deletedCount++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deletedCount;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
