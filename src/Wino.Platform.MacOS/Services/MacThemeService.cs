using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Platform.MacOS.Services;

/// <summary>Native appearance and account colors for the foundation. Wallpaper/gallery ports remain explicit.</summary>
public sealed class MacThemeService(IDispatcher dispatcher) : INewThemeService
{
    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;
    private ApplicationElementTheme _rootTheme;
    private string _accent = "#007aff";
    public bool IsCustomTheme => false;
    public Guid? CurrentApplicationThemeId { get; set; }
    public WindowBackdropType CurrentBackdropType { get; set; }
    public ApplicationElementTheme RootTheme
    {
        get => _rootTheme;
        set { _rootTheme = value; ElementThemeChanged?.Invoke(this, value); }
    }
    public string AccentColor { get => _accent; set { _accent = value; AccentColorChanged?.Invoke(this, value); } }
    public Task InitializeAsync() => ApplyThemeToActiveWindowAsync();
    public List<string> GetAvailableAccountColors() =>
        ["#e74c3c", "#c0392b", "#e53935", "#d81b60", "#e91e63", "#ec407a", "#ff4081", "#9b59b6", "#8e44ad", "#673ab7", "#3f51b5", "#3498db", "#2980b9", "#03a9f4", "#00bcd4", "#009688", "#1abc9c", "#16a085", "#2ecc71", "#27ae60", "#4caf50", "#8bc34a", "#cddc39", "#f1c40f", "#f39c12", "#ff9800", "#e67e22", "#d35400", "#795548", "#607d8b"];
    public string GetSystemAccentColorHex() => AccentColor;
    public Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        AccentColor = hexColor;
        return Task.CompletedTask;
    }
    public Task ApplyThemeToActiveWindowAsync() => dispatcher.ExecuteOnUIThread(() =>
        NSApplication.SharedApplication.Appearance = RootTheme switch
        {
            ApplicationElementTheme.Dark => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ApplicationElementTheme.Light => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            _ => null,
        });
    public Task ApplyCustomThemeAsync(bool isInitializing) => Task.FromException(Deferred());
    public Task<List<AppThemeBase>> GetAvailableThemesAsync() => Task.FromException<List<AppThemeBase>>(Deferred());
    public Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId) => Task.FromException<CustomThemeMetadata?>(Deferred());
    public Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request) => Task.FromException<CustomThemeMetadata>(Deferred());
    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => Task.FromException<List<CustomThemeMetadata>>(Deferred());
    public Task<bool> DeleteCustomThemeAsync(Guid themeId) => Task.FromException<bool>(Deferred());
    public Task SelectThemeAsync(Guid themeId, bool forceReapply = false) => Task.FromException(Deferred());
    public ThemeRuntimeState CaptureRuntimeState() => new(CurrentApplicationThemeId, CurrentApplicationThemeId ?? Guid.Empty, AccentColor, RootTheme);
    public Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme) => Task.FromException(Deferred());
    public Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        CurrentApplicationThemeId = state.ThemeId;
        AccentColor = state.AccentColor;
        RootTheme = state.ElementTheme;
        return ApplyThemeToActiveWindowAsync();
    }
    public void ApplyIconStyle() => throw Deferred();
    public void ApplyBackdrop(WindowBackdropType backdropType)
    {
        if (backdropType != WindowBackdropType.None) throw Deferred();
        CurrentBackdropType = backdropType;
        BackdropChanged?.Invoke(this, backdropType);
    }
    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() => [new(WindowBackdropType.None, "Default")];
    public void UpdateSystemCaptionButtonColors() => throw Deferred();
    private static PlatformNotSupportedException Deferred() => new("Custom theme and wallpaper presentation follows the OAuth/sidebar foundation.");
}
