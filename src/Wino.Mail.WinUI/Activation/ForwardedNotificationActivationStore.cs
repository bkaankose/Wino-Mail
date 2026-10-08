using System;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI.Activation;

internal static class ForwardedNotificationActivationStore
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    public static bool TryRead(string? launchArguments, bool deleteAfterRead, out NotificationHostActivation activation)
    {
        activation = null!;
        if (!NotificationHostLaunchArguments.TryParseForwardedActivation(launchArguments, out var activationId))
            return false;

        var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
        try
        {
            activation = NotificationHostFileStore.ReadActivation(localCachePath, activationId);
            var nowUtc = DateTimeOffset.UtcNow;
            return activation.CreatedAtUtc >= nowUtc - MaximumAge &&
                   activation.CreatedAtUtc <= nowUtc + MaximumFutureSkew;
        }
        catch
        {
            activation = null!;
            return false;
        }
        finally
        {
            if (deleteAfterRead)
                NotificationHostFileStore.TryDeleteActivation(localCachePath, activationId);
        }
    }
}
