using System;
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

        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation mailAction))
        {
            return mailAction == MailOperation.Navigate;
        }

        return true;
    }

    public static bool TryResolveMode(NotificationArguments toastArguments, out WinoApplicationMode mode)
    {
        mode = WinoApplicationMode.Mail;

        if (!toastArguments.TryGetValue(Constants.ToastModeKey, out string toastMode))
            return false;

        if (string.Equals(toastMode, Constants.ToastModeCalendar, StringComparison.OrdinalIgnoreCase))
        {
            mode = WinoApplicationMode.Calendar;
            return true;
        }

        if (string.Equals(toastMode, Constants.ToastModeMail, StringComparison.OrdinalIgnoreCase))
        {
            mode = WinoApplicationMode.Mail;
            return true;
        }

        return false;
    }

    private static bool ContainsKnownToastKey(NotificationArguments toastArguments)
        => toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastModeKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastActionKey, out string _);
}
