using Foundation;
using UserNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Notification categories and action identifiers shared by <see cref="AppleNotificationBuilder"/>
/// and the app's response handler. macOS shows at most four actions; mail offers the two
/// configured actions (as on Windows) plus Dismiss, calendar reminders Open / Join / Snooze / Dismiss.
/// The identifiers are the shared <see cref="NotificationActionIdentifiers"/>, which <see cref="NotificationResponse"/> carries back.
/// </summary>
public static class AppleNotificationCategories
{
    public const string Mail = "wino.mail";
    public const string MailSummary = "wino.mail.summary";

    public const string MailHidden = "wino.mail.hidden";
    public const string Calendar = "wino.calendar";
    public const string Generic = "wino.generic";

    /// <summary>Mail action identifiers are this prefix plus the <see cref="MailOperation"/> name.</summary>
    public const string MailActionPrefix = NotificationActionIdentifiers.MailActionPrefix;
    public const string OpenAction = NotificationActionIdentifiers.Open;
    public const string DismissAction = NotificationActionIdentifiers.Dismiss;
    public const string CalendarJoinAction = NotificationActionIdentifiers.CalendarJoin;
    public const string CalendarSnoozeAction = NotificationActionIdentifiers.CalendarSnooze;

    /// <summary>
    /// The snooze lengths a reminder can offer. macOS shows one Snooze action whose length is the
    /// "Default snooze duration" when the reminder allows it (Windows has a drop-down instead); each length
    /// has its own static category so the action title needs no re-registration when the setting changes.
    /// </summary>
    public static IReadOnlyList<int> CalendarSnoozeMinutes => CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes();

    /// <summary>Falls back to the fixed length notifications used before the setting was honoured.</summary>
    public const int FallbackSnoozeMinutes = NotificationActionIdentifiers.FallbackSnoozeMinutes;

    /// <summary>The operations Windows allows on a mail notification.</summary>
    public static MailOperation[] SupportedMailActions => NotificationActionIdentifiers.SupportedMailActions;

    public static string MailActionIdentifier(MailOperation operation) => NotificationActionIdentifiers.MailAction(operation);

    public static bool TryParseMailAction(string? identifier, out MailOperation operation)
        => NotificationActionIdentifiers.TryParseMailAction(identifier, out operation);

    /// <summary>Windows NotificationBuilder.GetConfiguredMailNotificationActions (shared rule).</summary>
    public static (MailOperation First, MailOperation Second) ResolveMailActions(MailOperation configuredFirst, MailOperation configuredSecond)
        => NotificationContentRules.ResolveMailActions(configuredFirst, configuredSecond);

    public static string MailActionTitle(MailOperation operation) => NotificationContentRules.MailActionTitle(operation);

    public static bool IsReplyAction(MailOperation operation) => NotificationContentRules.IsReplyAction(operation);

    public static string SnoozeTitle(int minutes) => NotificationContentRules.SnoozeTitle(minutes);

    /// <summary>A snooze length a category exists for; null when the reminder offers no snooze.</summary>
    public static int? NormalizeSnoozeMinutes(int? minutes)
        => minutes is { } value && CalendarSnoozeMinutes.Contains(value) ? value : null;

    /// <summary>Builds every category for the current mail action preferences.</summary>
    public static NSSet<UNNotificationCategory> Create(MailOperation firstMailAction, MailOperation secondMailAction)
    {
        var categories = new List<UNNotificationCategory>
        {
            Category(Mail, MailAction(firstMailAction), MailAction(secondMailAction), Dismiss()),
            Category(MailSummary, Dismiss()),
            Category(MailHidden, [.. new[] { firstMailAction, secondMailAction }
                .Where(action => !IsReplyAction(action))
                .Select(MailAction)
                .Append(Dismiss())])
        };
        foreach (var join in new[] { false, true })
        {
            categories.Add(CalendarCategoryFor(join, null));
            foreach (var minutes in CalendarSnoozeMinutes) categories.Add(CalendarCategoryFor(join, minutes));
        }
        categories.Add(Category(Generic,
            UNNotificationAction.FromIdentifier(OpenAction, Translator.Buttons_Open, UNNotificationActionOptions.Foreground),
            Dismiss()));
        return new NSSet<UNNotificationCategory>(categories.ToArray());
    }

    /// <summary>
    /// Reminder category: Join only when the event has an online meeting link, Snooze only when the
    /// reminder allows a snooze (Windows' allowed-snooze rule), titled with <paramref name="snoozeMinutes"/>.
    /// </summary>
    public static string CalendarCategory(bool join, int? snoozeMinutes)
        => Calendar + (join ? ".join" : string.Empty) + (snoozeMinutes is { } minutes ? ".snooze." + minutes : string.Empty);

    private static UNNotificationCategory CalendarCategoryFor(bool join, int? snoozeMinutes)
    {
        var actions = new List<UNNotificationAction>
        {
            UNNotificationAction.FromIdentifier(OpenAction, Translator.Buttons_Open, UNNotificationActionOptions.Foreground)
        };
        if (join) actions.Add(UNNotificationAction.FromIdentifier(CalendarJoinAction, Translator.CalendarEventDetails_JoinOnline, UNNotificationActionOptions.None));
        if (snoozeMinutes is { } minutes) actions.Add(UNNotificationAction.FromIdentifier(CalendarSnoozeAction, SnoozeTitle(minutes), UNNotificationActionOptions.None));
        actions.Add(Dismiss());
        return Category(CalendarCategory(join, snoozeMinutes), [.. actions]);
    }

    private static UNNotificationCategory Category(string identifier, params UNNotificationAction[] actions)
        => UNNotificationCategory.FromIdentifier(identifier, actions, [], UNNotificationCategoryOptions.None);

    private static UNNotificationAction Dismiss()
        => UNNotificationAction.FromIdentifier(DismissAction, Translator.Buttons_Dismiss, UNNotificationActionOptions.None);

    private static UNNotificationAction MailAction(MailOperation operation)
    {
        var identifier = MailActionIdentifier(operation);
        var title = MailActionTitle(operation);
        return operation switch
        {
            // Reply answers inline; the composer remains the fallback when sending is not possible.
            MailOperation.Reply => UNTextInputNotificationAction.FromIdentifier(identifier, title, UNNotificationActionOptions.None,
                Translator.Buttons_Send, Translator.MailNotification_ReplyPlaceholder),
            MailOperation.ReplyAll or MailOperation.Forward => UNNotificationAction.FromIdentifier(identifier, title, UNNotificationActionOptions.Foreground),
            MailOperation.SoftDelete => UNNotificationAction.FromIdentifier(identifier, title, UNNotificationActionOptions.Destructive),
            _ => UNNotificationAction.FromIdentifier(identifier, title, UNNotificationActionOptions.None)
        };
    }

    /// <summary>Reads a string value written by <see cref="ToUserInfo"/> (keys from <see cref="Constants"/>).</summary>
    public static string? GetValue(NSDictionary? userInfo, string key)
        => userInfo?.ObjectForKey(new NSString(key)) is NSString value ? value.ToString() : null;

    public static NSDictionary ToUserInfo(IReadOnlyDictionary<string, string> values)
    {
        var dictionary = new NSMutableDictionary();
        foreach (var pair in values) dictionary[new NSString(pair.Key)] = new NSString(pair.Value);
        return dictionary;
    }
}
