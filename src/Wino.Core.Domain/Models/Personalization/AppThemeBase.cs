using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// Base class for all app themes.
/// </summary>
public abstract class AppThemeBase
{
    public Guid Id { get; set; }
    public string ThemeName { get; set; }
    public ApplicationElementTheme ForceElementTheme { get; set; }
    public string AccentColor { get; set; }
    public bool IsAccentColorAssigned => !string.IsNullOrEmpty(AccentColor);
    public string BackgroundPreviewImage => GetBackgroundPreviewImagePath();
    public abstract AppThemeType AppThemeType { get; }

    protected AppThemeBase(string themeName, Guid id)
    {
        ThemeName = themeName;
        Id = id;
    }

    public abstract Task<string> GetThemeResourceDictionaryContentAsync();
    public abstract string GetBackgroundPreviewImagePath();
}
