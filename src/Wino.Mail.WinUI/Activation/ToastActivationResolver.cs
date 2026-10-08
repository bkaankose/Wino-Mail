using Microsoft.Windows.AppNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class ToastActivationResolver
{
    public static bool TryParse(string? argument, out NotificationArguments toastArguments)
    {
        toastArguments = default!;

        if (string.IsNullOrWhiteSpace(argument))
            return false;

        try
        {
            var parsedArguments = NotificationArguments.Parse(argument);
            if (!ContainsKnownToastKey(parsedArguments))
                return false;

            toastArguments = parsedArguments;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ShouldBringToForeground(NotificationArguments toastArguments)
    {
        if (toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string storeUpdateAction) &&
            storeUpdateAction == Constants.ToastStoreUpdateActionInstall)
        {
            return true;
        }

        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction))
        {
            return calendarAction == Constants.ToastCalendarNavigateAction;
        }

        if (toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _))
        {
            return false;
        }

        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation mailAction))
        {
            return mailAction is MailOperation.Navigate or MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;
        }

        return true;
    }

    public static bool TryResolveMode(NotificationArguments toastArguments, out WinoApplicationMode mode)
    {
        mode = WinoApplicationMode.Mail;
        if (!toastArguments.TryGetValue(Constants.ToastModeKey, out string value))
            return false;

        mode = value switch
        {
            Constants.ToastModeMail => WinoApplicationMode.Mail,
            Constants.ToastModeCalendar => WinoApplicationMode.Calendar,
            Constants.ToastModePeople => WinoApplicationMode.Contacts,
            Constants.ToastModeTasks => WinoApplicationMode.Tasks,
            _ => mode
        };

        return value is Constants.ToastModeMail or
            Constants.ToastModeCalendar or
            Constants.ToastModePeople or
            Constants.ToastModeTasks;
    }

    private static bool ContainsKnownToastKey(NotificationArguments toastArguments)
        => toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastModeKey, out string _);
}
