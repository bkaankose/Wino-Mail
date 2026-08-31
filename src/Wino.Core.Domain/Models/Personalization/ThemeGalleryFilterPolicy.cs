using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public static class ThemeGalleryFilterPolicy
{
    public static IReadOnlyList<AppThemeBase> Apply(IEnumerable<AppThemeBase> themes, Guid? currentThemeId, ThemeGalleryFilter filter)
        => themes.Where(theme => theme.Id != currentThemeId).Where(theme => Matches(theme, filter)).ToList();

    public static bool Matches(AppThemeBase theme, ThemeGalleryFilter filter)
        => filter switch
        {
            ThemeGalleryFilter.All => true,
            ThemeGalleryFilter.Custom => theme.AppThemeType == AppThemeType.Custom,
            ThemeGalleryFilter.Both => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility == ThemeCompatibility.Both,
            ThemeGalleryFilter.Dark => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility is ThemeCompatibility.Dark or ThemeCompatibility.Both,
            ThemeGalleryFilter.Light => theme.AppThemeType != AppThemeType.Custom && theme.Compatibility is ThemeCompatibility.Light or ThemeCompatibility.Both,
            ThemeGalleryFilter.Online => false,
            _ => false
        };
}
