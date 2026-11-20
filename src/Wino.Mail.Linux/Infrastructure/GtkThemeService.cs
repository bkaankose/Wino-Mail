using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Theme state for the Linux proof of concept: the selection, accent and element theme are kept in memory.
/// libadwaita follows the system colour scheme, so there are no Wino themes to list yet.
/// </summary>
internal sealed class GtkThemeService : INewThemeService
{
    private Guid? _themeId;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    public WindowBackdropType CurrentBackdropType { get; set; } = WindowBackdropType.None;
    public ApplicationElementTheme RootTheme { get; set; } = ApplicationElementTheme.Default;
    public Guid? CurrentApplicationThemeId { get => _themeId; set => _themeId = value; }
    public string AccentColor { get; set; } = string.Empty;
    public bool IsCustomTheme => _themeId is { } id && !WinoThemeCatalog.IsPredefined(id);

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task<List<AppThemeBase>> GetAvailableThemesAsync() => Task.FromResult(new List<AppThemeBase>());

    public Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId) => Task.FromResult<CustomThemeMetadata?>(null);

    public Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
    {
        return Task.FromException<CustomThemeMetadata>(new NotSupportedException("Custom themes are not available on Linux yet."));
    }

    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => Task.FromResult(new List<CustomThemeMetadata>());

    public Task<bool> DeleteCustomThemeAsync(Guid themeId) => Task.FromResult(false);

    public Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        _themeId = themeId;
        return Task.CompletedTask;
    }

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? WinoThemeCatalog.DefaultThemeId, AccentColor, RootTheme);

    public Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme)
    {
        return Task.CompletedTask;
    }

    public Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        _themeId = state.ThemeId;
        AccentColor = state.AccentColor ?? string.Empty;
        RootTheme = state.ElementTheme;
        return Task.CompletedTask;
    }

    public List<string> GetAvailableAccountColors() => [];

    public Task ApplyCustomThemeAsync(bool isInitializing) => Task.CompletedTask;

    public void ApplyIconStyle() { }

    public void ApplyBackdrop(WindowBackdropType backdropType)
    {
        CurrentBackdropType = backdropType;
        BackdropChanged?.Invoke(this, backdropType);
    }

    public string GetSystemAccentColorHex() => string.Empty;

    public Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        AccentColor = hexColor ?? string.Empty;
        AccentColorChanged?.Invoke(this, AccentColor);
        return Task.CompletedTask;
    }

    public void UpdateSystemCaptionButtonColors() { }

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() => [];

    public Task ApplyThemeToActiveWindowAsync()
    {
        ElementThemeChanged?.Invoke(this, RootTheme);
        return Task.CompletedTask;
    }
}
