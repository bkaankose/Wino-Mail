using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Core.Domain.Interfaces;

public interface IThemeService : IInitializeAsync
{
    event EventHandler<ApplicationElementTheme> ElementThemeChanged;
    event EventHandler<string> AccentColorChanged;

    Task<List<AppThemeBase>> GetAvailableThemesAsync();
    Task<CustomThemeMetadata> CreateNewCustomThemeAsync(string themeName, string accentColor, byte[] wallpaperData);
    Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync();

    Task ApplyCustomThemeAsync(bool isInitializing);

    // Settings
    ApplicationElementTheme RootTheme { get; set; }
    Guid CurrentApplicationThemeId { get; set; }
    string AccentColor { get; set; }
    string GetSystemAccentColorHex();
    bool IsCustomTheme { get; }
}
