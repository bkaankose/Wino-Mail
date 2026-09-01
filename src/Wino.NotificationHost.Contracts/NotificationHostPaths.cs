namespace Wino.NotificationHost.Contracts;

public static class NotificationHostPaths
{
    public const string RootFolderName = "NotificationHost";
    public const string RequestsFolderName = "Requests";
    public const string ActivationsFolderName = "Activations";

    public static string GetRequestDirectory(string localCachePath)
        => GetChildDirectory(localCachePath, RequestsFolderName);

    public static string GetActivationDirectory(string localCachePath)
        => GetChildDirectory(localCachePath, ActivationsFolderName);

    public static string GetRequestPath(string localCachePath, Guid requestId)
        => GetEnvelopePath(GetRequestDirectory(localCachePath), requestId);

    public static string GetActivationPath(string localCachePath, Guid activationId)
        => GetEnvelopePath(GetActivationDirectory(localCachePath), activationId);

    private static string GetChildDirectory(string localCachePath, string childFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localCachePath);
        return Path.Combine(localCachePath, RootFolderName, childFolder);
    }

    private static string GetEnvelopePath(string directory, Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Envelope ID cannot be empty.", nameof(id));

        return Path.Combine(directory, $"{id:N}.bin");
    }
}
