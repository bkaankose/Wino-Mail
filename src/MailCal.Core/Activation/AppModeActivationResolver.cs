#nullable enable
using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Activation;

public static class AppModeActivationResolver
{
    public static MailCalApplicationMode Resolve(string? launchArguments, string? tileId, string? appId, MailCalApplicationMode defaultMode = MailCalApplicationMode.Mail)
    {
        if (TryResolveFromText(launchArguments, defaultMode, out var mode))
            return mode;

        if (TryResolveFromText(tileId, defaultMode, out mode))
            return mode;

        if (TryResolveFromText(appId, defaultMode, out mode))
            return mode;

        return defaultMode;
    }

    private static bool TryResolveFromText(string? value, MailCalApplicationMode defaultMode, out MailCalApplicationMode mode)
    {
        mode = defaultMode;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Contains(value, "--mode=toggle-default") ||
            Contains(value, "mode=toggle-default"))
        {
            mode = GetOpposite(defaultMode);
            return true;
        }

        if (Contains(value, "--mailcal-calendar") ||
            Contains(value, "mailcal-calendar") ||
            Contains(value, "--mode=calendar") ||
            Contains(value, "mode=calendar") ||
            Contains(value, "calendarapp") ||
            EqualsToken(value, "calendar"))
        {
            mode = MailCalApplicationMode.Calendar;
            return true;
        }

        if (Contains(value, "--mailcal-todo") ||
            Contains(value, "mailcal-todo") ||
            Contains(value, "--mode=tasks") ||
            Contains(value, "mode=tasks") ||
            Contains(value, "todoapp") ||
            Contains(value, "tasksapp") ||
            EqualsToken(value, "todo") ||
            EqualsToken(value, "tasks"))
        {
            mode = MailCalApplicationMode.Tasks;
            return true;
        }

        if (Contains(value, "--mailcal-people") ||
            Contains(value, "mailcal-people") ||
            Contains(value, "--mode=people") ||
            Contains(value, "mode=people") ||
            Contains(value, "peopleapp") ||
            EqualsToken(value, "people") ||
            Contains(value, "mailcal-contacts") ||
            Contains(value, "--mode=contacts") ||
            Contains(value, "mode=contacts") ||
            Contains(value, "contactsapp") ||
            EqualsToken(value, "contacts"))
        {
            mode = MailCalApplicationMode.Contacts;
            return true;
        }

        if (Contains(value, "mailcal-settings") ||
            Contains(value, "--mode=settings") ||
            Contains(value, "mode=settings") ||
            Contains(value, "settingsapp") ||
            EqualsToken(value, "settings"))
        {
            mode = MailCalApplicationMode.Settings;
            return true;
        }

        if (Contains(value, "--mailcal-mail") ||
            Contains(value, "mailcal-mail") ||
            Contains(value, "--mode=mail") ||
            Contains(value, "mode=mail") ||
            Contains(value, "!app") ||
            Contains(value, "mailapp") ||
            EqualsToken(value, "app") ||
            EqualsToken(value, "mail"))
        {
            mode = MailCalApplicationMode.Mail;
            return true;
        }

        return false;
    }

    private static bool Contains(string source, string token)
        => source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsToken(string source, string token)
        => string.Equals(source.Trim(), token, StringComparison.OrdinalIgnoreCase);

    private static MailCalApplicationMode GetOpposite(MailCalApplicationMode defaultMode)
        => defaultMode switch
        {
            MailCalApplicationMode.Mail => MailCalApplicationMode.Calendar,
            MailCalApplicationMode.Calendar => MailCalApplicationMode.Mail,
            _ => MailCalApplicationMode.Mail
        };
}
