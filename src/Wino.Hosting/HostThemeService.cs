using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Hosting;

/// <summary>
/// Theme state for a host with no window: the selection, accent and element theme are kept in memory and recorded.
/// Nothing is drawn, so there are no themes to list and custom themes cannot be created.
/// </summary>
internal sealed class HostThemeService(HostInteractionLog log) : INewThemeService
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
        log.Record(HostInteractionKind.Theme, nameof(InitializeAsync));
        return Task.CompletedTask;
    }

    public Task<List<AppThemeBase>> GetAvailableThemesAsync() => Task.FromResult(new List<AppThemeBase>());

    public Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId) => Task.FromResult<CustomThemeMetadata?>(null);

    public Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
    {
        log.Record(HostInteractionKind.Theme, nameof(SaveCustomThemeAsync), request.Name, "not supported");
        return Task.FromException<CustomThemeMetadata>(new NotSupportedException("Custom themes need a window; this host has none."));
    }

    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => Task.FromResult(new List<CustomThemeMetadata>());

    public Task<bool> DeleteCustomThemeAsync(Guid themeId) => Task.FromResult(false);

    public Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        _themeId = themeId;
        log.Record(HostInteractionKind.Theme, nameof(SelectThemeAsync), themeId.ToString());
        return Task.CompletedTask;
    }

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? WinoThemeCatalog.DefaultThemeId, AccentColor, RootTheme);

    public Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme)
    {
        log.Record(HostInteractionKind.Theme, nameof(PreviewCustomThemeAsync), metadata.Name);
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
        log.Record(HostInteractionKind.Theme, nameof(SetAccentColorAsync), AccentColor);
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
