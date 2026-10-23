namespace Wino.Core.Domain.Models.Notifications;

public static class NotificationContentRules
{
    public static string HiddenMailTitle => Translator.Notifications_MultipleNotificationsTitle;

    public static string ComposeHiddenMailText(int count)
        => string.Format(count == 1
            ? Translator.Notifications_HiddenContentMessage_Singular
            : Translator.Notifications_HiddenContentMessage_Plural, count);

    public static string HiddenCalendarReminderTitle => Translator.CalendarReminder_HiddenContentTitle;

    public static string ComposeHiddenCalendarReminderText()
        => Translator.CalendarReminder_HiddenContentMessage;
}
