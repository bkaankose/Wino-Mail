using Android.App;
using Android.Content;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Platform.Android.Notifications;

/// <summary>
/// Notification channels (API 26+): one per mail account in the "Mail accounts" group at default importance, so the user
/// can tune each account in system settings, plus new-mail summaries, calendar reminders, account alerts, and the silent channel of
/// the background send. Below API 26 channels do not exist and the ids are ignored.
/// </summary>
public static class AndroidNotificationChannels
{
    public const string AccountGroup = "wino.accounts";
    public const string MailPrefix = "wino.mail.";
    public const string MailSummary = "wino.mail";
    public const string Calendar = "wino.calendar";
    public const string Account = "wino.account";
    public const string Background = "wino.background";

    public static string ForAccount(Guid accountId) => MailPrefix + accountId.ToString("N");

    /// <summary>True for the mail channels (per account and the summary), which use default importance.</summary>
    public static bool IsMail(string channel) => channel == MailSummary || channel.StartsWith(MailPrefix, StringComparison.Ordinal);

    /// <summary>Creates the fixed channels and one channel per account; removes channels of accounts that no longer exist.</summary>
    public static void Ensure(Context context, IReadOnlyList<MailAccount> accounts)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;

        EnsureFixed(context);

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var id = ForAccount(account.Id);
            current.Add(id);
            manager.CreateNotificationChannel(AccountChannel(id, account));
        }

        foreach (var channel in manager.NotificationChannels ?? [])
        {
            if (channel.Id is { } id && id.StartsWith(MailPrefix, StringComparison.Ordinal) && !current.Contains(id))
                manager.DeleteNotificationChannel(id);
        }
    }

    /// <summary>The channels that do not depend on accounts (the background send needs its channel before services are ready).</summary>
    public static void EnsureFixed(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;

        manager.CreateNotificationChannelGroup(new NotificationChannelGroup(AccountGroup, Translator.Android_NotificationChannelGroup_Accounts));
        manager.CreateNotificationChannel(Channel(MailSummary, Translator.Notifications_MultipleNotificationsTitle, null, NotificationImportance.Default));
        manager.CreateNotificationChannel(Channel(Calendar, Translator.Android_NotificationChannel_Calendar, Translator.Android_NotificationChannel_CalendarDescription, NotificationImportance.High));
        manager.CreateNotificationChannel(Channel(Account, Translator.Android_NotificationChannel_Account, Translator.Android_NotificationChannel_AccountDescription, NotificationImportance.Default));
        var background = Channel(Background, Translator.Android_NotificationChannel_Background, Translator.Android_NotificationChannel_BackgroundDescription, NotificationImportance.Min);
        background.SetShowBadge(false);
        background.SetSound(null, null);
        manager.CreateNotificationChannel(background);
    }

    /// <summary>Creates (or renames) the channel of one account before a notification is posted to it.</summary>
    public static void EnsureAccount(Context context, MailAccount account)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        manager.CreateNotificationChannelGroup(new NotificationChannelGroup(AccountGroup, Translator.Android_NotificationChannelGroup_Accounts));
        manager.CreateNotificationChannel(AccountChannel(ForAccount(account.Id), account));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static NotificationChannel AccountChannel(string id, MailAccount account)
    {
        var name = string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? id : account.Name;
        // Default importance (sound, no heads-up) as on the canvas Notification board; calendar reminders stay high.
        var channel = Channel(id, name, string.Format(Translator.Android_NotificationChannel_MailDescription, account.Address ?? name), NotificationImportance.Default);
        channel.Group = AccountGroup;
        return channel;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static NotificationChannel Channel(string id, string name, string? description, NotificationImportance importance)
    {
        var channel = new NotificationChannel(id, name, importance);
        if (!string.IsNullOrWhiteSpace(description)) channel.Description = description;
        channel.SetShowBadge(true);
        return channel;
    }
}
