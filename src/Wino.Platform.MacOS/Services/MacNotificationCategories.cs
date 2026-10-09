using Foundation;
using UserNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Notification categories and action identifiers shared by <see cref="MacNotificationBuilder"/>
/// and the app's response handler. macOS shows at most four actions; mail offers the two
/// configured actions (as on Windows) plus Dismiss, calendar reminders Open / Join / Snooze / Dismiss.
/// </summary>
public static class MacNotificationCategories
{
    public const string Mail = "wino.mail";
    public const string MailSummary = "wino.mail.summary";
    public const string Calendar = "wino.calendar";
    public const string Generic = "wino.generic";

    /// <summary>Mail action identifiers are this prefix plus the <see cref="MailOperation"/> name.</summary>
    public const string MailActionPrefix = "wino.mail.";
    public const string OpenAction = "wino.open";
    public const string DismissAction = "wino.dismiss";
    public const string CalendarJoinAction = "wino.calendar.joinonline";
    public const string CalendarSnoozeAction = "wino.calendar.snooze";

    /// <summary>
    /// The snooze lengths a reminder can offer. macOS shows one Snooze action whose length is the
    /// "Default snooze duration" when the reminder allows it (Windows has a drop-down instead); each length
    /// has its own static category so the action title needs no re-registration when the setting changes.
    /// </summary>
    public static IReadOnlyList<int> CalendarSnoozeMinutes => CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes();

    /// <summary>Falls back to the fixed length notifications used before the setting was honoured.</summary>
    public const int FallbackSnoozeMinutes = 5;

    /// <summary>The operations Windows allows on a mail notification.</summary>
    public static readonly MailOperation[] SupportedMailActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    public static string MailActionIdentifier(MailOperation operation) => MailActionPrefix + operation;

    public static bool TryParseMailAction(string? identifier, out MailOperation operation)
    {
        operation = MailOperation.None;
        return identifier is not null &&
               identifier.StartsWith(MailActionPrefix, StringComparison.Ordinal) &&
               Enum.TryParse(identifier[MailActionPrefix.Length..], out operation) &&
               SupportedMailActions.Contains(operation);
    }

    /// <summary>Windows NotificationBuilder.GetConfiguredMailNotificationActions.</summary>
    public static (MailOperation First, MailOperation Second) ResolveMailActions(MailOperation configuredFirst, MailOperation configuredSecond)
    {
        var first = SupportedMailActions.Contains(configuredFirst) ? configuredFirst : MailOperation.MarkAsRead;
        var second = SupportedMailActions.Contains(configuredSecond) ? configuredSecond : MailOperation.SoftDelete;
        if (second == first) second = SupportedMailActions.First(action => action != first);
        return (first, second);
    }

    public static string MailActionTitle(MailOperation operation) => operation switch
    {
        MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
        MailOperation.SoftDelete => Translator.MailOperation_Delete,
        MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
        MailOperation.Archive => Translator.MailOperation_Archive,
        MailOperation.Reply => Translator.MailOperation_Reply,
        MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
        MailOperation.Forward => Translator.MailOperation_Forward,
        _ => operation.ToString()
    };

    public static string SnoozeTitle(int minutes) => $"{Translator.CalendarReminder_SnoozeAction} ({string.Format(Translator.CalendarReminder_SnoozeMinutesOption, minutes)})";

    /// <summary>A snooze length a category exists for; null when the reminder offers no snooze.</summary>
    public static int? NormalizeSnoozeMinutes(int? minutes)
        => minutes is { } value && CalendarSnoozeMinutes.Contains(value) ? value : null;

    /// <summary>Builds every category for the current mail action preferences.</summary>
    public static NSSet<UNNotificationCategory> Create(MailOperation firstMailAction, MailOperation secondMailAction)
    {
        var categories = new List<UNNotificationCategory>
        {
            Category(Mail, MailAction(firstMailAction), MailAction(secondMailAction), Dismiss()),
            Category(MailSummary, Dismiss())
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
