using CommunityToolkit.Mvvm.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Personalization. On iOS appearance follows the system or a Light/Dark override, the accent can be overridden, and
/// a Wino theme sets the accent and a backdrop behind the panes. Themes are picked and created in the theme gallery,
/// which the "Application themes" card opens, as on Windows. iOS has no window material choice, so the Mac material
/// card is absent.
/// </summary>
/// <remarks>
/// The shared ViewModel's initialization drives the Windows accent and backdrop pickers, which iOS lays out natively,
/// so this page reads and writes <see cref="INewThemeService"/> through <see cref="UIKitAppearanceModel"/> and keeps
/// the ViewModel for preferences and commands (Mac PersonalizationPageViewController).
/// </remarks>
public sealed class PersonalizationPageViewController(PersonalizationPageViewModel viewModel, UIKitThemeService themeService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<PersonalizationPageViewModel>(viewModel, dispatcher, logger)
{
    // The Windows accent palette (PersonalizationPageViewModel.InitializeColors).
    private static readonly string[] AccentPalette =
    [
        "#0078d7", "#00838c", "#e3008c", "#ca4f07", "#e81123", "#00819e", "#10893e", "#881798",
        "#c239b3", "#767676", "#e1b12c", "#16a085", "#0984e3", "#4a69bd", "#05c46b"
    ];

    private readonly UIKitAppearanceModel _appearance = new(themeService);
    private readonly UILabel _currentThemeName = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
    private readonly ThemeThumbnailView _currentThemePreview = new(64, 40);

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        Bindings.Own(_appearance);

        var swatches = new WinoColorSwatchPicker { Colors = AccentPalette };
        Bind.Bind(_appearance, nameof(UIKitAppearanceModel.AccentHex), s => s.AccentHex, hex => swatches.SelectedHex = hex);
        EventHandler<string> picked = (_, hex) => _appearance.AccentHex = hex;
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        Bind.Enabled(swatches, _appearance, nameof(UIKitAppearanceModel.UseSystemAccent), s => !s.UseSystemAccent);

        var accent = Expander(Translator.SettingsAccentColor_Title, Translator.SettingsAccentColor_Description, WinoIconGlyph.Color, null);
        accent.Add(swatches, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
        // The check button is its own row under the swatches (no empty card title above it).
        accent.Add(Bind.Checkbox(_appearance, Translator.SettingsAccentColor_UseSystemAccentColor, nameof(UIKitAppearanceModel.UseSystemAccent),
            s => s.UseSystemAccent, (s, v) => s.UseSystemAccent = v), 4, WinoSettingsStyle.CardPadding, 4, WinoSettingsStyle.CardPadding);

        // Three and two segments do not fit beside the text on a phone: the control goes under it.
        var appearance = StackedCard(Translator.SettingsElementTheme_Title, Translator.SettingsElementTheme_SelectThemeDescription, WinoIconGlyph.DarkTheme,
            Bind.Segmented(_appearance, vm.ElementThemes.Select(theme => theme.Title).ToArray(),
                nameof(UIKitAppearanceModel.ThemeIndex), s => s.ThemeIndex, (s, v) => s.ThemeIndex = v));

        var iconStyle = StackedCard(Translator.SettingsIconStyle_Title, Translator.SettingsIconStyle_Description, WinoIconGlyph.IconStyle,
            Bind.Segmented(p, vm.IconStyles.Select(style => style.Title).ToArray(), nameof(p.IconStyle),
                s => vm.IconStyles.FindIndex(style => style.IconStyle == s.IconStyle),
                (s, v) => { if (v >= 0 && v < vm.IconStyles.Count) s.IconStyle = vm.IconStyles[v].IconStyle; }));

        AddGroup(null, accent, appearance, iconStyle);

        // Wino themes: one entry point into the theme gallery, like the Windows Personalization page.
        var themeContent = WinoLayout.HStack(WinoStyle.Space2, _currentThemeName, _currentThemePreview);
        themeContent.Alignment = UIStackViewAlignment.Center;
        AddGroup(null, NavigationCard(Translator.ApplicationThemeGallery_Title, Translator.ApplicationThemeGallery_Description, WinoIconGlyph.PaintBrush,
            () => vm.NavigateApplicationThemesCommand.Execute(null), themeContent));
        EventHandler appearanceChanged = (_, _) => _ = RefreshCurrentThemeAsync();
        UIKitThemeService.AppearanceChanged += appearanceChanged;
        Bindings.Own(new ActionDisposable(() => UIKitThemeService.AppearanceChanged -= appearanceChanged));

        var backdrop = Card(Translator.MacOS_Personalization_BackdropTitle, Translator.IOS_Personalization_BackdropDescription, WinoIconGlyph.Color,
            Bind.Switch(_appearance, nameof(UIKitAppearanceModel.IsBackdropEnabled), s => s.IsBackdropEnabled, (s, v) => s.IsBackdropEnabled = v,
                Translator.MacOS_Personalization_BackdropTitle));

        AddGroup(null,
            backdrop,
            Card(Translator.SettingsCompactAccountMenuItem_Title, Translator.SettingsCompactAccountMenuItem_Description, WinoIconGlyph.List,
                Bind.Switch(p, nameof(p.IsCompactAccountMenuItemEnabled), s => s.IsCompactAccountMenuItemEnabled, (s, v) => s.IsCompactAccountMenuItemEnabled = v, Translator.SettingsCompactAccountMenuItem_Title)),
            Card(Translator.SettingsAppPreferences_HideWinoAccountButton_Title, Translator.SettingsAppPreferences_HideWinoAccountButton_Description, WinoIconGlyph.Person,
                Bind.Switch(p, nameof(p.IsWinoAccountButtonHidden), s => s.IsWinoAccountButtonHidden, (s, v) => s.IsWinoAccountButtonHidden = v, Translator.SettingsAppPreferences_HideWinoAccountButton_Title)));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        // Deliberately not ViewModel.OnNavigatedTo: see the class remarks.
        => RefreshCurrentThemeAsync();

    protected override Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>Shows the current theme's name and preview on the gallery card.</summary>
    private async Task RefreshCurrentThemeAsync()
    {
        try
        {
            var themes = await themeService.GetAvailableThemesAsync();
            var currentId = themeService.CurrentApplicationThemeId ?? UIKitThemeService.DefaultThemeId;
            var current = themes.FirstOrDefault(theme => theme.Id == currentId) ?? themes.FirstOrDefault();
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _currentThemeName.Text = current?.ThemeName ?? string.Empty;
                _currentThemePreview.Show(current);
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }
}

/// <summary>Observable adapter over the iOS theme service for the Personalization page (Mac MacAppearanceModel).</summary>
public sealed class UIKitAppearanceModel : ObservableObject, IDisposable
{
    private static readonly ApplicationElementTheme[] Themes = [ApplicationElementTheme.Default, ApplicationElementTheme.Light, ApplicationElementTheme.Dark];
    private readonly UIKitThemeService _themes;
    private string? _lastAccent;

    public UIKitAppearanceModel(UIKitThemeService themes)
    {
        _themes = themes;
        _themes.ElementThemeChanged += ThemeChanged;
        _themes.AccentColorChanged += AccentChanged;
        UIKitThemeService.AppearanceChanged += AppearanceChanged;
        _lastAccent = string.IsNullOrEmpty(_themes.AccentColor) ? null : _themes.AccentColor;
    }

    public int ThemeIndex
    {
        get => Array.IndexOf(Themes, _themes.RootTheme);
        set
        {
            if (value < 0 || value >= Themes.Length || Themes[value] == _themes.RootTheme) return;
            // The service setter stores the choice and applies it.
            _themes.RootTheme = Themes[value];
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
        UIKitThemeService.AppearanceChanged -= AppearanceChanged;
    }
}
