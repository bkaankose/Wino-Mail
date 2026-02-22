using System;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class AppModeActivationResolver
{
    public static WinoApplicationMode Resolve(string? launchArguments, string? tileId, string? appId, WinoApplicationMode defaultMode = WinoApplicationMode.Mail)
    {
        if (TryResolveFromText(launchArguments, out var mode))
            return mode;

        if (TryResolveFromText(tileId, out mode))
            return mode;

        if (TryResolveFromText(appId, out mode))
            return mode;

        return defaultMode;
    }

    private static bool TryResolveFromText(string? value, out WinoApplicationMode mode)
    {
        mode = WinoApplicationMode.Mail;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Contains(value, "wino-calendar") ||
            Contains(value, "--mode=calendar") ||
            Contains(value, "mode=calendar") ||
            Contains(value, "calendarapp") ||
            EqualsToken(value, "calendar"))
        {
            mode = WinoApplicationMode.Calendar;
            return true;
        }

        if (Contains(value, "wino-mail") ||
            Contains(value, "--mode=mail") ||
            Contains(value, "mode=mail") ||
            Contains(value, "mailapp") ||
            EqualsToken(value, "mail"))
        {
            mode = WinoApplicationMode.Mail;
            return true;
        }

        return false;
    }

    private static bool Contains(string source, string token)
        => source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsToken(string source, string token)
        => string.Equals(source.Trim(), token, StringComparison.OrdinalIgnoreCase);
}
