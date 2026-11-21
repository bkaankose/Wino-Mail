using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Personalization (Windows PersonalizationPage) through the shared appearance model, as on macOS: accent, light or
/// dark, the theme gallery, app modes, icon style, compact account rows and the Wino Account button.
/// <see cref="PersonalizationPageViewModel.OnNavigatedTo"/> is not called: it drives the Windows accent grid and backdrop pickers.
/// </summary>
internal sealed class PersonalizationPage : SettingsPageView<PersonalizationPageViewModel>
{
    private readonly INewThemeService _themes;
    private readonly LinuxAppearanceModel _appearance;
    private readonly Gtk.Label _currentThemeName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _currentThemePreview = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Picture _currentThemePicture = Gtk.Picture.New();
    private string? _currentGradient;
    private Guid? _currentThemeId;
    private Adw.ExpanderRow? _modes;
    private readonly List<Gtk.Widget> _modeRows = [];
    private SettingsBinder? _modeBinder;

    public PersonalizationPage(PersonalizationPageViewModel viewModel, WinoViewServices services, INewThemeService themes) : base(viewModel, services)
    {
        _themes = themes;
        _appearance = Bindings.Own(new LinuxAppearanceModel(themes));
    }

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var swatches = new WinoColorSwatches(Translator.SettingsAccentColor_Title) { Colors = AppearanceSettingsModel.AccentPalette };
        Settings.Own(new ActionDisposable(swatches.Release));
        Settings.Bind(_appearance, nameof(_appearance.AccentHex), static s => s.AccentHex, hex => swatches.SelectedHex = hex);
        Settings.Bind(_appearance, nameof(_appearance.UseSystemAccent), static s => !s.UseSystemAccent, enabled => swatches.IsEnabled = enabled);
        swatches.SelectionChanged += hex => _appearance.AccentHex = hex;
        var accent = WinoSettingsRows.Expander(Translator.SettingsAccentColor_Title, Translator.SettingsAccentColor_Description, WinoIconGlyph.Color, false,
            WinoSettingsRows.Custom(swatches.Root, Translator.SettingsAccentColor_Title),
            Settings.Switch(_appearance, Translator.SettingsAccentColor_UseSystemAccentColor, null, WinoIconGlyph.None,
                nameof(_appearance.UseSystemAccent), static s => s.UseSystemAccent, static (s, v) => s.UseSystemAccent = v));

        var theme = Settings.Choice(_appearance, Translator.SettingsElementTheme_Title, null, WinoIconGlyph.DarkTheme,
            vm.ElementThemes.Select(static t => t.Title).ToArray(),
            nameof(_appearance.ThemeIndex), static s => s.ThemeIndex, static (s, v) => s.ThemeIndex = v);
        Settings.Bind(_appearance, nameof(_appearance.CanSelectElementTheme), static s => s.CanSelectElementTheme, canSelect =>
        {
            theme.SetSensitive(canSelect);
            theme.SetSubtitle(canSelect ? Translator.SettingsElementTheme_SelectThemeDescription : Translator.SettingsElementThemeSelectionDisabled);
        });
        AddGroup(null, accent, theme);

        _currentThemeName.AddCssClass("dim-label");
        _currentThemeName.SetEllipsize(Pango.EllipsizeMode.End);
        _currentThemePicture.SetContentFit(Gtk.ContentFit.Cover);
        _currentThemePicture.SetCanShrink(true);
        _currentThemePicture.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _currentThemePreview.AddCssClass("wino-theme-preview");
        _currentThemePreview.SetOverflow(Gtk.Overflow.Hidden);
        _currentThemePreview.SetSizeRequest(64, 40);
        _currentThemePreview.SetHexpand(false);
        _currentThemePreview.Append(WinoThemeTile.Clamped(_currentThemePicture, 64));
        WinoSwatchCss.EnsureInstalled();
        var detail = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        detail.Append(_currentThemeName);
        detail.Append(_currentThemePreview);
        AddGroup(null, Settings.Command(Translator.ApplicationThemeGallery_Title, Translator.ApplicationThemeGallery_Description, WinoIconGlyph.PaintBrush,
            vm.NavigateApplicationThemesCommand, detail: detail));
        WinoAppearance.Changed.Add(this, static page => page.RunOnUI(() => page.Observe(page.RefreshCurrentThemeAsync())));
        Settings.Own(new ActionDisposable(() => WinoAppearance.Changed.Remove(this)));

        _modes = WinoSettingsRows.Expander(Translator.SettingsPersonalization_AppModes_Title, Translator.SettingsPersonalization_AppModes_Description, WinoIconGlyph.Grid);
        AddGroup(null, _modes);
        Settings.Collection(vm.AppModeOptions, RebuildModes);

        // No window backdrop row: GetAvailableBackdropTypes is empty on Linux; wallpapers come with Wino themes.
        AddGroup(null,
            Settings.Choice(p, Translator.SettingsIconStyle_Title, Translator.SettingsIconStyle_Description, WinoIconGlyph.IconStyle,
                vm.IconStyles.Select(static s => s.Title).ToArray(), nameof(p.IconStyle),
                s => vm.IconStyles.FindIndex(style => style.IconStyle == s.IconStyle),
                (s, v) =>
                {
                    if (v >= 0 && v < vm.IconStyles.Count) s.IconStyle = vm.IconStyles[v].IconStyle;
                }),
            Settings.Switch(p, Translator.SettingsCompactAccountMenuItem_Title, Translator.SettingsCompactAccountMenuItem_Description, WinoIconGlyph.List,
                nameof(p.IsCompactAccountMenuItemEnabled), static s => s.IsCompactAccountMenuItemEnabled, static (s, v) => s.IsCompactAccountMenuItemEnabled = v),
            Settings.Switch(p, Translator.SettingsAppPreferences_HideWinoAccountButton_Title, Translator.SettingsAppPreferences_HideWinoAccountButton_Description, WinoIconGlyph.Person,
                nameof(p.IsWinoAccountButtonHidden), static s => s.IsWinoAccountButtonHidden, static (s, v) => s.IsWinoAccountButtonHidden = v));
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter)
        => Task.WhenAll(RefreshCurrentThemeAsync(), ViewModel.ActivateAppModeOptionsAsync());

    // Not the base: OnNavigatedFrom undoes what the unused OnNavigatedTo subscribed.
    protected override Task DeactivateAsync()
    {
        ViewModel.DeactivateAppModeOptions();
        return Task.CompletedTask;
    }

    private void RebuildModes()
    {
        if (_modes is null) return;
        _modeBinder?.Scope.Dispose();
        foreach (var row in _modeRows) _modes.Remove(row);
        _modeRows.Clear();
        var rows = _modeBinder = Settings.Child();
        foreach (var option in ViewModel.AppModeOptions.ToList())
        {
            var row = rows.Switch(option, option.Title, option.Description, option.Icon,
                nameof(option.IsShown), static o => o.IsShown, static (o, v) => o.IsShown = v);
            rows.Subtitle(row, option, nameof(option.Description), static o => o.Description);
            rows.Enabled(row, option, nameof(option.CanToggle), static o => o.CanToggle);
            _modes.AddRow(row);
            _modeRows.Add(row);
        }
    }

    private async Task RefreshCurrentThemeAsync()
    {
        var themes = await _themes.GetAvailableThemesAsync();
        var currentId = _themes.CurrentApplicationThemeId ?? WinoThemeCatalog.DefaultThemeId;
        var current = themes.FirstOrDefault(theme => theme.Id == currentId) ?? themes.FirstOrDefault();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            _currentThemeName.SetText(current?.ThemeName ?? string.Empty);
            _currentThemeId = current?.Id;
            if (_currentGradient is not null) _currentThemePreview.RemoveCssClass(_currentGradient);
            _currentGradient = null;
            _currentThemePicture.SetPaintable(null);
            if (current is null) return;
            var model = ThemePreviews.Model(current);
            if (model.GradientStart is { } start && model.GradientEnd is { } end)
            {
                _currentGradient = WinoSwatchCss.Gradient(start, end);
                _currentThemePreview.AddCssClass(_currentGradient);
            }
            else if (model.ImagePath is { } path)
            {
                var shown = current.Id;
                Observe(ThemePreviews.LoadAsync(path, Dispatcher, texture =>
                {
                    if (!IsReleased && shown == _currentThemeId) _currentThemePicture.SetPaintable(texture);
                }));
            }
        });
    }
}
