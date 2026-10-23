using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Activation;

internal static class AppModeActivationResolver
{
    public static MailCalApplicationMode Resolve(string? launchArguments, string? tileId, string? appId, MailCalApplicationMode defaultMode = MailCalApplicationMode.Mail)
        => MailCal.Core.Activation.AppModeActivationResolver.Resolve(launchArguments, tileId, appId, defaultMode);

    public static bool TryResolveExplicit(string? launchArguments, string? tileId, string? appId, out MailCalApplicationMode mode)
    {
        var mailDefaultMode = Resolve(launchArguments, tileId, appId, MailCalApplicationMode.Mail);
        var calendarDefaultMode = Resolve(launchArguments, tileId, appId, MailCalApplicationMode.Calendar);
        var contactsDefaultMode = Resolve(launchArguments, tileId, appId, MailCalApplicationMode.Contacts);
        var tasksDefaultMode = Resolve(launchArguments, tileId, appId, MailCalApplicationMode.Tasks);

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
