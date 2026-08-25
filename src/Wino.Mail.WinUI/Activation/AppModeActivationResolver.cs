using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class AppModeActivationResolver
{
    public static WinoApplicationMode Resolve(string? launchArguments, string? tileId, string? appId, WinoApplicationMode defaultMode = WinoApplicationMode.Mail)
        => Wino.Core.Activation.AppModeActivationResolver.Resolve(launchArguments, tileId, appId, defaultMode);

    public static bool TryResolveExplicit(string? launchArguments, string? tileId, string? appId, out WinoApplicationMode mode)
    {
        var mailDefaultMode = Resolve(launchArguments, tileId, appId, WinoApplicationMode.Mail);
        var calendarDefaultMode = Resolve(launchArguments, tileId, appId, WinoApplicationMode.Calendar);
        var contactsDefaultMode = Resolve(launchArguments, tileId, appId, WinoApplicationMode.Contacts);
        var tasksDefaultMode = Resolve(launchArguments, tileId, appId, WinoApplicationMode.Tasks);

        if (mailDefaultMode == calendarDefaultMode &&
            mailDefaultMode == contactsDefaultMode &&
            mailDefaultMode == tasksDefaultMode)
        {
            mode = mailDefaultMode;
            return true;
        }

        mode = default;
        return false;
    }
}
