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
    Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId);
    Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request);
    Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync();
    Task<bool> DeleteCustomThemeAsync(Guid themeId);
    Task SelectThemeAsync(Guid themeId, bool forceReapply = false);
    ThemeRuntimeState CaptureRuntimeState();
    Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme);
    Task RestoreRuntimeStateAsync(ThemeRuntimeState state);
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

    // Title bar color management
    void UpdateSystemCaptionButtonColors();

    // Backdrop management
    List<BackdropTypeWrapper> GetAvailableBackdropTypes();

    /// <summary>
    /// Re-applies the current theme (backdrop, root theme, accent, caption colors)
    /// to the window selected by the window manager. Call before making a newly
    /// created window visible so WinUI cannot render its default light theme first.
    /// </summary>
    Task ApplyThemeToActiveWindowAsync();
}
