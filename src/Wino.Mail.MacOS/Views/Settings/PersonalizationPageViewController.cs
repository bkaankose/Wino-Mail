using AppKit;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Personalization. On macOS appearance follows the system or a Light/Dark override, the accent can
/// be overridden, and a Wino theme sets the accent and a backdrop behind list and reader while the
/// sidebar keeps its native material. The custom theme editor is deferred on Mac.
/// </summary>
/// <remarks>
/// The shared ViewModel's initialization asks the theme service for the theme gallery, which the Mac
/// foundation does not provide yet, so this page reads and writes <see cref="INewThemeService"/> through
/// <see cref="MacAppearanceModel"/> and keeps the ViewModel for preferences and commands.
/// </remarks>
public sealed class PersonalizationPageViewController(PersonalizationPageViewModel viewModel, MacWinoThemeService themeService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<PersonalizationPageViewModel>(viewModel, dispatcher, logger)
{
    // The Windows accent palette (PersonalizationPageViewModel.InitializeColors).
    private static readonly string[] AccentPalette =
    [
        "#0078d7", "#00838c", "#e3008c", "#ca4f07", "#e81123", "#00819e", "#10893e", "#881798",
        "#c239b3", "#767676", "#e1b12c", "#16a085", "#0984e3", "#4a69bd", "#05c46b"
    ];

    private readonly MacAppearanceModel _appearance = new(themeService);
    private readonly NSStackView _themes = WinoLayout.VStack(10);
    private readonly List<(Guid Id, WinoThemeTile Tile)> _themeTiles = new();

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        Bindings.Own(_appearance);

        var swatches = new WinoColorSwatchPicker { Colors = AccentPalette };
        Bind.Bind(_appearance, nameof(MacAppearanceModel.AccentHex), s => s.AccentHex, hex => swatches.SelectedHex = hex);
        EventHandler<string> picked = (_, hex) => _appearance.AccentHex = hex;
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        Bind.Enabled(swatches, _appearance, nameof(MacAppearanceModel.UseSystemAccent), s => !s.UseSystemAccent);
        var swatchCard = Card(string.Empty, null, WinoIconGlyph.None);
        swatchCard.Content = swatches;
        swatches.WidthAnchor.ConstraintEqualTo(300).Active = true;

        var accent = Expander(Translator.SettingsAccentColor_Title, Translator.SettingsAccentColor_Description, WinoIconGlyph.Color, null,
            swatchCard,
            Card(string.Empty, null, WinoIconGlyph.None,
                Bind.Checkbox(_appearance, Translator.SettingsAccentColor_UseWindowsAccentColor, nameof(MacAppearanceModel.UseSystemAccent),
                    s => s.UseSystemAccent, (s, v) => s.UseSystemAccent = v)));

        var appearance = Card(Translator.SettingsElementTheme_Title, Translator.SettingsElementTheme_Description, WinoIconGlyph.DarkTheme,
            Bind.Segmented(_appearance, vm.ElementThemes.Select(theme => theme.Title).ToList(),
                nameof(MacAppearanceModel.ThemeIndex), s => s.ThemeIndex, (s, v) => s.ThemeIndex = v));

        var iconStyle = Card(Translator.SettingsIconStyle_Title, Translator.SettingsIconStyle_Description, WinoIconGlyph.IconStyle,
            Bind.Segmented(p, vm.IconStyles.Select(style => style.Title).ToList(), nameof(p.IconStyle),
                s => vm.IconStyles.FindIndex(style => style.IconStyle == s.IconStyle),
                (s, v) => { if (v >= 0 && v < vm.IconStyles.Count) s.IconStyle = vm.IconStyles[v].IconStyle; }));

        AddGroup(null, accent, appearance, iconStyle);

        // Wino themes: filled once the theme service answers; otherwise a deferred notice.
        var themeGroup = new WinoSettingsGroup(Translator.ApplicationThemeGallery_Title, Translator.ApplicationThemeGallery_Description);
        _themes.Alignment = NSLayoutAttribute.Leading;
        _themes.Spacing = 14;
        var themeHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_themes, themeHost, 4, 1, 8, 0);
        themeGroup.Add(themeHost);
        var customTheme = Card(Translator.ApplicationThemeEditor_CreateTitle, ComingLaterOnMac, WinoIconGlyph.PaintBrush);
        customTheme.IsEnabled = false;
        themeGroup.Add(customTheme);
        Add(themeGroup);

        var backdrop = Card(BackdropTitle, BackdropDescription, WinoIconGlyph.Color,
            Bind.Switch(_appearance, nameof(MacAppearanceModel.IsBackdropEnabled), s => s.IsBackdropEnabled, (s, v) => s.IsBackdropEnabled = v, BackdropTitle));

        AddGroup(null,
            backdrop,
            Card(Translator.SettingsCompactAccountMenuItem_Title, Translator.SettingsCompactAccountMenuItem_Description, WinoIconGlyph.List,
                Bind.Switch(p, nameof(p.IsCompactAccountMenuItemEnabled), s => s.IsCompactAccountMenuItemEnabled, (s, v) => s.IsCompactAccountMenuItemEnabled = v, Translator.SettingsCompactAccountMenuItem_Title)),
            Card(Translator.SettingsAppPreferences_HideWinoAccountButton_Title, Translator.SettingsAppPreferences_HideWinoAccountButton_Description, WinoIconGlyph.Person,
                Bind.Switch(p, nameof(p.IsWinoAccountButtonHidden), s => s.IsWinoAccountButtonHidden, (s, v) => s.IsWinoAccountButtonHidden = v, Translator.SettingsAppPreferences_HideWinoAccountButton_Title)));
    }

    /// <summary>Gradient theme stops (light start/end, dark start/end), mirrored from MacWinoThemeService.</summary>
    private static readonly Dictionary<string, uint[]> GradientThemes = new()
    {
        ["Mist"] = [0xF4F7FA, 0xE1E7EF, 0x191D23, 0x0C0E12],
        ["Cocoa"] = [0xFBF7F2, 0xEDE3D8, 0x1D1815, 0x100D0B],
        ["Moss"] = [0xF1F6F1, 0xE0EBE1, 0x141A16, 0x0B0F0C],
        ["Rose"] = [0xFBF2F5, 0xF2E2E8, 0x1B1418, 0x100B0E],
        ["Indigo"] = [0xF1F3FB, 0xE1E5F5, 0x12141F, 0x0A0C14],
    };

    private static string ComingLaterOnMac => Translator.MacOS_Personalization_CustomThemesLater;
    private static string BackdropTitle => Translator.MacOS_Personalization_BackdropTitle;
    private static string BackdropDescription => Translator.MacOS_Personalization_BackdropDescription;

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // Deliberately not ViewModel.OnNavigatedTo: see the class remarks.
        List<AppThemeBase>? themes = null;
        try { themes = await themeService.GetAvailableThemesAsync(); }
        catch (Exception exception) when (exception is NotSupportedException or NotImplementedException or PlatformNotSupportedException or InvalidOperationException) { }
        catch (Exception exception) { ReportError(exception); }
        await Dispatcher.ExecuteOnUIThread(() => BuildThemeGrid(themes));
    }

    protected override Task DeactivateAsync() => Task.CompletedTask;

    private void BuildThemeGrid(List<AppThemeBase>? themes)
    {
        foreach (var view in _themes.ArrangedSubviews) { _themes.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        _themeTiles.Clear();
        if (themes is not { Count: > 0 })
        {
            var notice = WinoStyle.Label(SettingsPlaceholderViewController.LaterMessage, WinoStyle.Description, WinoStyle.SecondaryText, 0);
            _themes.AddArrangedSubview(notice);
            return;
        }

        const int columns = 4;
        for (int start = 0; start < themes.Count; start += columns)
        {
            var row = WinoLayout.HStack(18);
            row.Alignment = NSLayoutAttribute.Top;
            for (int index = start; index < Math.Min(start + columns, themes.Count); index++)
            {
                var theme = themes[index];
                var name = theme.ThemeName ?? string.Empty;
                GradientThemes.TryGetValue(name, out var gradient);
                var tile = new WinoThemeTile(name, gradient is null ? MacWinoThemeService.PreviewImage(name) : null, gradient,
                    theme.ForceElementTheme == ApplicationElementTheme.Dark);
                var id = theme.Id;
                tile.Pressed += async (_, _) =>
                {
                    try
                    {
                        await themeService.SelectThemeAsync(id);
                        await Dispatcher.ExecuteOnUIThread(UpdateThemeSelection);
                    }
                    catch (Exception exception) { ReportError(exception); }
                };
                _themeTiles.Add((id, tile));
                row.AddArrangedSubview(tile);
            }
            _themes.AddArrangedSubview(row);
        }
        UpdateThemeSelection();
    }

    private void UpdateThemeSelection()
    {
        var current = themeService.CurrentApplicationThemeId;
        for (int index = 0; index < _themeTiles.Count; index++)
            _themeTiles[index].Tile.IsSelected = current is { } id ? _themeTiles[index].Id == id : index == 0;
    }
}

/// <summary>Observable adapter over the Mac theme service for the Personalization page.</summary>
public sealed class MacAppearanceModel : ObservableObject, IDisposable
{
    private static readonly ApplicationElementTheme[] Themes = [ApplicationElementTheme.Default, ApplicationElementTheme.Light, ApplicationElementTheme.Dark];
    private readonly MacWinoThemeService _themes;
    private string? _lastAccent;

    public MacAppearanceModel(MacWinoThemeService themes)
    {
        _themes = themes;
        _themes.ElementThemeChanged += ThemeChanged;
        _themes.AccentColorChanged += AccentChanged;
        MacWinoThemeService.AppearanceChanged += AppearanceChanged;
        _lastAccent = string.IsNullOrEmpty(_themes.AccentColor) ? null : _themes.AccentColor;
    }

    public int ThemeIndex
    {
        get => Array.IndexOf(Themes, _themes.RootTheme);
        set
        {
            if (value < 0 || value >= Themes.Length || Themes[value] == _themes.RootTheme) return;
            _themes.RootTheme = Themes[value];
            _ = _themes.ApplyThemeToActiveWindowAsync();
            OnPropertyChanged();
        }
    }

    /// <summary>The overriding accent, or null when the system accent is used.</summary>
    public string? AccentHex
    {
        get => string.IsNullOrEmpty(_themes.AccentColor) ? null : _themes.AccentColor;
        set
        {
            if (string.Equals(value ?? string.Empty, _themes.AccentColor ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(value)) _lastAccent = value;
            _themes.AccentColor = value ?? string.Empty;
        }
    }

    public bool UseSystemAccent
    {
        get => string.IsNullOrEmpty(_themes.AccentColor);
        set
        {
            if (value == UseSystemAccent) return;
            AccentHex = value ? null : _lastAccent ?? "#0078d7";
        }
    }

    public bool IsBackdropEnabled
    {
        get => _themes.IsBackdropEnabled;
        set
        {
            if (value == _themes.IsBackdropEnabled) return;
            _themes.IsBackdropEnabled = value;
            OnPropertyChanged();
        }
    }

    private void AppearanceChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(IsBackdropEnabled));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(AccentHex));
        OnPropertyChanged(nameof(UseSystemAccent));
    }

    private void ThemeChanged(object? sender, ApplicationElementTheme theme) => OnPropertyChanged(nameof(ThemeIndex));

    private void AccentChanged(object? sender, string accent)
    {
        OnPropertyChanged(nameof(AccentHex));
        OnPropertyChanged(nameof(UseSystemAccent));
    }

    public void Dispose()
    {
        _themes.ElementThemeChanged -= ThemeChanged;
        _themes.AccentColorChanged -= AccentChanged;
        MacWinoThemeService.AppearanceChanged -= AppearanceChanged;
    }
}
