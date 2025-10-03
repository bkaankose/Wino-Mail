using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Core.Domain.Interfaces;

public interface INewThemeService : IInitializeAsync
{
    event EventHandler<ApplicationElementTheme> ElementThemeChanged;
    event EventHandler<string> AccentColorChanged;
    event EventHandler<WindowBackdropType> BackdropChanged;

    Task<List<AppThemeBase>> GetAvailableThemesAsync();
    Task<CustomThemeMetadata> CreateNewCustomThemeAsync(string themeName, string accentColor, byte[] wallpaperData);
    Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync();
    List<string> GetAvailableAccountColors();
    Task ApplyCustomThemeAsync(bool isInitializing);

    // Window Backdrop Management
    WindowBackdropType CurrentBackdropType { get; set; }
    void ApplyBackdrop(WindowBackdropType backdropType);

    // Settings
    ApplicationElementTheme RootTheme { get; set; }
    Guid? CurrentApplicationThemeId { get; set; }
    string AccentColor { get; set; }
    string GetSystemAccentColorHex();
    bool IsCustomTheme { get; }

    // Improved accent color management
    Task SetAccentColorAsync(string hexColor, bool preserveTheme = true);

    // Backdrop management
    List<BackdropTypeWrapper> GetAvailableBackdropTypes();
}
