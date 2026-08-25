using Windows.ApplicationModel;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class AppEntryConstants
{
    public const string MailApplicationId = "App";
    public const string CalendarApplicationId = "CalendarApp";
    public const string ContactsApplicationId = "ContactsApp";
    public const string MailLaunchArgument = "--wino-mail";
    public const string CalendarLaunchArgument = "--wino-calendar";
    public const string ContactsLaunchArgument = "--wino-contacts";

    public static string GetModeLaunchArgument(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Calendar => CalendarLaunchArgument,
            WinoApplicationMode.Contacts => ContactsLaunchArgument,
            WinoApplicationMode.Settings => "--mode=settings",
            _ => MailLaunchArgument
        };

    public static string? GetPackagedApplicationId(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Calendar => CalendarApplicationId,
            WinoApplicationMode.Contacts => ContactsApplicationId,
            WinoApplicationMode.Mail => MailApplicationId,
            _ => null
        };

    public static string GetAppUserModelId(string packageFamilyName, WinoApplicationMode mode)
        => $"{packageFamilyName}!{GetPackagedApplicationId(mode) ?? MailApplicationId}";

    public static string GetAppUserModelId(WinoApplicationMode mode)
        => GetAppUserModelId(Package.Current.Id.FamilyName, mode);
}
