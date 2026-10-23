using Windows.ApplicationModel;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Activation;

internal static class AppEntryConstants
{
    public const string MailApplicationId = "App";
    public const string CalendarApplicationId = "CalendarApp";
    public const string ContactsApplicationId = "ContactsApp";
    public const string ToDoApplicationId = "ToDoApp";
    public const string MailLaunchArgument = "--mailcal-mail";
    public const string CalendarLaunchArgument = "--mailcal-calendar";
    public const string ContactsLaunchArgument = "--mailcal-contacts";
    public const string ToDoLaunchArgument = "--mailcal-todo";

    public static string GetModeLaunchArgument(MailCalApplicationMode mode)
        => mode switch
        {
            MailCalApplicationMode.Calendar => CalendarLaunchArgument,
            MailCalApplicationMode.Contacts => ContactsLaunchArgument,
            MailCalApplicationMode.Tasks => ToDoLaunchArgument,
            MailCalApplicationMode.Settings => "--mode=settings",
            _ => MailLaunchArgument
        };

    public static string? GetPackagedApplicationId(MailCalApplicationMode mode)
        => mode switch
        {
            MailCalApplicationMode.Calendar => CalendarApplicationId,
            MailCalApplicationMode.Contacts => ContactsApplicationId,
            MailCalApplicationMode.Tasks => ToDoApplicationId,
            MailCalApplicationMode.Mail => MailApplicationId,
            _ => null
        };

    public static string GetAppUserModelId(string packageFamilyName, MailCalApplicationMode mode)
        => $"{packageFamilyName}!{GetPackagedApplicationId(mode) ?? MailApplicationId}";

    public static string GetAppUserModelId(MailCalApplicationMode mode)
        => GetAppUserModelId(Package.Current.Id.FamilyName, mode);
}
