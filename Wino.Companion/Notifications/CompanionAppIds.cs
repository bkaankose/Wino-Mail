using Windows.ApplicationModel;

namespace Wino.Companion.Notifications;

internal static class CompanionAppIds
{
    public const string MailApplicationId = "App";
    public const string MailLaunchArgument = "--wino-mail";

    public static string MailAumid => $"{Package.Current.Id.FamilyName}!{MailApplicationId}";
}
