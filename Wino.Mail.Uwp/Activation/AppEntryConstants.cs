using Windows.ApplicationModel;

namespace Wino.Mail.Uwp.Activation;

public static class AppEntryConstants
{
    public const string MailApplicationId = "App";

    public static string MailAppUserModelId => GetAppUserModelId(MailApplicationId);

    public static string GetAppUserModelId(string applicationId) =>
        $"{Package.Current.Id.FamilyName}!{applicationId}";
}
