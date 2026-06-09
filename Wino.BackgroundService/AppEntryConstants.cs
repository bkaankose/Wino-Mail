using Windows.ApplicationModel;

namespace Wino.BackgroundService;

/// <summary>
/// App list entry ids of the packaged applications (mirror of the UI-side constants).
/// </summary>
internal static class AppEntryConstants
{
    public const string MailApplicationId = "App";
    public const string CalendarApplicationId = "CalendarApp";

    public static string GetAppUserModelId(string applicationId)
        => $"{Package.Current.Id.FamilyName}!{applicationId}";
}
