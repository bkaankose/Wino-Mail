#nullable enable
using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Settings;

/// <summary>
/// Reads and applies the theme and layout that a sync snapshot carries. The backend only stores the values; this
/// service owns the theme and shell state, so an export passes <see cref="Capture"/> on the selection and an import
/// applies <see cref="WinoAccountSyncImportResult.Appearance"/> here on the UI thread.
/// </summary>
public sealed class AppearanceSnapshotService(INewThemeService? themeService = null, IStatePersistanceService? stateService = null)
{
    public SyncSnapshotAppearance? Capture()
    {
        if (themeService == null && stateService == null) return null;

        return new SyncSnapshotAppearance
        {
            RootTheme = (int)(themeService?.RootTheme ?? ApplicationElementTheme.Default),
            ThemeId = themeService?.CurrentApplicationThemeId,
            AccentColor = themeService?.AccentColor,
            BackdropType = (int)(themeService?.CurrentBackdropType ?? WindowBackdropType.Mica),
            OpenPaneLength = stateService?.OpenPaneLength ?? 0,
            MailListPaneLength = stateService?.MailListPaneLength ?? 0,
            CalendarDisplayType = (int)(stateService?.CalendarDisplayType ?? CalendarDisplayType.Week),
            DayDisplayCount = stateService?.DayDisplayCount ?? 0
        };
    }

    /// <summary>Must run on the UI thread.</summary>
    public void Apply(SyncSnapshotAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        if (themeService != null)
        {
            if (Enum.IsDefined((WindowBackdropType)appearance.BackdropType))
            {
                themeService.CurrentBackdropType = (WindowBackdropType)appearance.BackdropType;
            }

            if (Enum.IsDefined((ApplicationElementTheme)appearance.RootTheme))
            {
                themeService.RootTheme = (ApplicationElementTheme)appearance.RootTheme;
            }

            if (appearance.AccentColor != null && !string.Equals(themeService.AccentColor, appearance.AccentColor, StringComparison.OrdinalIgnoreCase))
            {
                themeService.AccentColor = appearance.AccentColor;
            }

            // Built-in theme ids are the same on every device. A custom theme only exists where it
            // was made, so an unknown id is left alone rather than selecting nothing.
            if (appearance.ThemeId is { } themeId && themeId != themeService.CurrentApplicationThemeId)
            {
                themeService.CurrentApplicationThemeId = themeId;
            }
        }

        if (stateService != null)
        {
            if (appearance.OpenPaneLength > 0) stateService.OpenPaneLength = appearance.OpenPaneLength;
            if (appearance.MailListPaneLength > 0) stateService.MailListPaneLength = appearance.MailListPaneLength;
            if (Enum.IsDefined((CalendarDisplayType)appearance.CalendarDisplayType)) stateService.CalendarDisplayType = (CalendarDisplayType)appearance.CalendarDisplayType;
            if (appearance.DayDisplayCount > 0) stateService.DayDisplayCount = appearance.DayDisplayCount;
        }
    }
}
