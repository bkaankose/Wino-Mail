using Foundation;
using UIKit;
using Wino.Core.Domain;

namespace Wino.Mail.UIKit.Infrastructure;

public enum QuickActionKind
{
    NewMail,
    NewEvent,
    NewTask,
    NextMeeting
}

/// <summary>The Home Screen quick action types (long-press on the app icon) and their payload.</summary>
public static class QuickActionIdentifiers
{
    public const string NewMail = "com.winomail.quickaction.new-mail";
    public const string NewEvent = "com.winomail.quickaction.new-event";
    public const string NewTask = "com.winomail.quickaction.new-task";
    public const string NextMeeting = "com.winomail.quickaction.next-meeting";

    public static bool TryParse(UIApplicationShortcutItem item, out QuickActionKind kind, out Guid? calendarItemId)
    {
        calendarItemId = null;
        switch (item.Type)
        {
            case NewMail: kind = QuickActionKind.NewMail; return true;
            case NewEvent: kind = QuickActionKind.NewEvent; return true;
            case NewTask: kind = QuickActionKind.NewTask; return true;
            case NextMeeting:
                kind = QuickActionKind.NextMeeting;
                if (item.UserInfo?[Constants.ToastCalendarItemIdKey] is NSString value && Guid.TryParse(value, out var id)) calendarItemId = id;
                return calendarItemId is not null;
            default:
                kind = default;
                return false;
        }
    }

    public static NSDictionary<NSString, NSObject> CalendarItemPayload(Guid calendarItemId)
        => new(new NSString(Constants.ToastCalendarItemIdKey), new NSString(calendarItemId.ToString()));
}
