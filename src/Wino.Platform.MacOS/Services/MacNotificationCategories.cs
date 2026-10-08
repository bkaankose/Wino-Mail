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

    /// <summary>The single snooze length offered on macOS (Windows has a drop-down).</summary>
    public const int CalendarSnoozeMinutes = 5;

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

    public static string SnoozeTitle => $"{Translator.CalendarReminder_SnoozeAction} ({string.Format(Translator.CalendarReminder_SnoozeMinutesOption, CalendarSnoozeMinutes)})";

    /// <summary>Builds every category for the current mail action preferences.</summary>
    public static NSSet<UNNotificationCategory> Create(MailOperation firstMailAction, MailOperation secondMailAction)
    {
        var categories = new[]
        {
            Category(Mail, MailAction(firstMailAction), MailAction(secondMailAction), Dismiss()),
            Category(MailSummary, Dismiss()),
            CalendarCategoryFor(join: false, snooze: false),
            CalendarCategoryFor(join: false, snooze: true),
            CalendarCategoryFor(join: true, snooze: false),
            CalendarCategoryFor(join: true, snooze: true),
            Category(Generic,
                UNNotificationAction.FromIdentifier(OpenAction, Translator.Buttons_Open, UNNotificationActionOptions.Foreground),
                Dismiss())
        };
        return new NSSet<UNNotificationCategory>(categories);
    }

    /// <summary>
    /// Reminder category: Join only when the event has an online meeting link, Snooze only when
    /// the reminder leaves room for <see cref="CalendarSnoozeMinutes"/> (Windows' allowed-snooze rule).
    /// </summary>
    public static string CalendarCategory(bool join, bool snooze)
        => Calendar + (join ? ".join" : string.Empty) + (snooze ? ".snooze" : string.Empty);

    private static UNNotificationCategory CalendarCategoryFor(bool join, bool snooze)
    {
        var actions = new List<UNNotificationAction>
        {
            UNNotificationAction.FromIdentifier(OpenAction, Translator.Buttons_Open, UNNotificationActionOptions.Foreground)
        };
        if (join) actions.Add(UNNotificationAction.FromIdentifier(CalendarJoinAction, Translator.CalendarEventDetails_JoinOnline, UNNotificationActionOptions.None));
        if (snooze) actions.Add(UNNotificationAction.FromIdentifier(CalendarSnoozeAction, SnoozeTitle, UNNotificationActionOptions.None));
        actions.Add(Dismiss());
        return Category(CalendarCategory(join, snooze), [.. actions]);
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
