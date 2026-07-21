using Windows.Storage;

namespace Wino.Companion.Services;

public enum CompanionCloseBehavior
{
    RunInBackgroundWithTrayIcon = 0,
    RunInBackgroundWithoutTrayIcon,
    Terminate,
}

public sealed class CompanionPreferences
{
    public int EmailSyncIntervalMinutes
    {
        get
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            return values.TryGetValue("EmailSyncIntervalMinutes", out var configured) &&
                   int.TryParse(configured?.ToString(), out var minutes)
                ? Math.Clamp(minutes, 1, 1_440)
                : 3;
        }
    }

    public CompanionCloseBehavior CloseBehavior
    {
        get
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (values.TryGetValue("AppCloseBehavior", out var configured) &&
                Enum.TryParse<CompanionCloseBehavior>(configured?.ToString(), out var behavior))
            {
                return behavior;
            }

            if (values.TryGetValue("IsSystemTrayIconEnabled", out var legacy) &&
                bool.TryParse(legacy?.ToString(), out var legacyTrayEnabled))
            {
                return legacyTrayEnabled
                    ? CompanionCloseBehavior.RunInBackgroundWithTrayIcon
                    : CompanionCloseBehavior.Terminate;
            }

            return CompanionCloseBehavior.RunInBackgroundWithTrayIcon;
        }
    }
}
