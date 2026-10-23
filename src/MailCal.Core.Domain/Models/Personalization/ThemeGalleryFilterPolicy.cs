using System;
using System.Collections.Generic;
using System.Linq;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Personalization;

public static class ThemeGalleryFilterPolicy
{
    public static IReadOnlyList<AppThemeBase> Apply(IEnumerable<AppThemeBase> themes, Guid? currentThemeId, ThemeGalleryFilter filter)
        => filter == ThemeGalleryFilter.Online
            ? Array.Empty<AppThemeBase>()
            : themes.Where(theme => Matches(theme, filter)).ToList();

    public static bool Matches(AppThemeBase theme, ThemeGalleryFilter filter)
        => filter switch
        {
            ThemeGalleryFilter.All => true,
            ThemeGalleryFilter.Custom => theme.AppThemeType == AppThemeType.Custom,
            ThemeGalleryFilter.Both => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility == ThemeCompatibility.Both,
            ThemeGalleryFilter.Dark => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility == ThemeCompatibility.Dark,
            ThemeGalleryFilter.Light => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility == ThemeCompatibility.Light,
            ThemeGalleryFilter.Online => false,
            _ => false
        };
}
