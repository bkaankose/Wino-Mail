using AppKit;
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
/// sidebar keeps its native material. Themes are picked and created in the theme gallery, which the
/// "Application themes" card opens, as on Windows.
/// </summary>
/// <remarks>
/// The shared ViewModel's initialization drives the Windows accent and backdrop pickers, which the Mac
/// lays out natively, so this page reads and writes <see cref="INewThemeService"/> through
/// <see cref="MacAppearanceModel"/> and keeps the ViewModel for preferences and commands.
/// </remarks>
public sealed class PersonalizationPageViewController(PersonalizationPageViewModel viewModel, MacWinoThemeService themeService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<PersonalizationPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly MacAppearanceModel _appearance = new(themeService);
    private readonly NSTextField _currentThemeName = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
    private readonly ThemeThumbnailView _currentThemePreview = new(64, 40);

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        Bindings.Own(_appearance);

        var swatches = new WinoColorSwatchPicker { Colors = AppearanceSettingsModel.AccentPalette };
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
                Bind.Checkbox(_appearance, Translator.SettingsAccentColor_UseSystemAccentColor, nameof(MacAppearanceModel.UseSystemAccent),
                    s => s.UseSystemAccent, (s, v) => s.UseSystemAccent = v)));

        var appearance = Card(Translator.SettingsElementTheme_Title, Translator.SettingsElementTheme_SelectThemeDescription, WinoIconGlyph.DarkTheme,
            Bind.Segmented(_appearance, vm.ElementThemes.Select(theme => theme.Title).ToList(),
                nameof(MacAppearanceModel.ThemeIndex), s => s.ThemeIndex, (s, v) => s.ThemeIndex = v));

        var iconStyle = Card(Translator.SettingsIconStyle_Title, Translator.SettingsIconStyle_Description, WinoIconGlyph.IconStyle,
            Bind.Segmented(p, vm.IconStyles.Select(style => style.Title).ToList(), nameof(p.IconStyle),
                s => vm.IconStyles.FindIndex(style => style.IconStyle == s.IconStyle),
                (s, v) => { if (v >= 0 && v < vm.IconStyles.Count) s.IconStyle = vm.IconStyles[v].IconStyle; }));

        var iconSet = Card(Translator.SettingsIconSet_Title, Translator.SettingsIconSet_Description, WinoIconGlyph.Grid,
            Bind.PopUp(p, vm.IconSets.Select(set => set.Title).ToList(), nameof(p.IconSet),
                s => vm.IconSets.FindIndex(set => set.IconSet == s.IconSet),
                (s, v) => { if (v >= 0 && v < vm.IconSets.Count) s.IconSet = vm.IconSets[v].IconSet; }));

        AddGroup(null, accent, appearance, iconSet, iconStyle);

        // Wino themes: one entry point into the theme gallery, like the Windows Personalization page.
        var themeContent = WinoLayout.HStack(WinoStyle.Space2, _currentThemeName, _currentThemePreview);
        themeContent.Alignment = NSLayoutAttribute.CenterY;
        AddGroup(null, NavigationCard(Translator.ApplicationThemeGallery_Title, Translator.ApplicationThemeGallery_Description, WinoIconGlyph.PaintBrush,
            () => vm.NavigateApplicationThemesCommand.Execute(null), themeContent));
        EventHandler appearanceChanged = (_, _) => _ = RefreshCurrentThemeAsync();
        MacWinoThemeService.AppearanceChanged += appearanceChanged;
        Bindings.Own(new ActionDisposable(() => MacWinoThemeService.AppearanceChanged -= appearanceChanged));

        var backdrop = Card(BackdropTitle, BackdropDescription, WinoIconGlyph.Color,
            Bind.Switch(_appearance, nameof(MacAppearanceModel.IsBackdropEnabled), s => s.IsBackdropEnabled, (s, v) => s.IsBackdropEnabled = v, BackdropTitle));

        var material = Card(Translator.MacOS_Personalization_WindowMaterialTitle, Translator.MacOS_Personalization_WindowMaterialDescription, WinoIconGlyph.Desktop,
            Bind.Segmented(_appearance, [Translator.MacOS_Personalization_WindowMaterialDefault, Translator.MacOS_Personalization_WindowMaterialTranslucent],
                nameof(MacAppearanceModel.MaterialIndex), s => s.MaterialIndex, (s, v) => s.MaterialIndex = v));

        AddGroup(null,
            backdrop,
            material,
            Card(Translator.SettingsCompactAccountMenuItem_Title, Translator.SettingsCompactAccountMenuItem_Description, WinoIconGlyph.List,
                Bind.Switch(p, nameof(p.IsCompactAccountMenuItemEnabled), s => s.IsCompactAccountMenuItemEnabled, (s, v) => s.IsCompactAccountMenuItemEnabled = v, Translator.SettingsCompactAccountMenuItem_Title)),
            Card(Translator.SettingsAppPreferences_HideWinoAccountButton_Title, Translator.SettingsAppPreferences_HideWinoAccountButton_Description, WinoIconGlyph.Person,
                Bind.Switch(p, nameof(p.IsWinoAccountButtonHidden), s => s.IsWinoAccountButtonHidden, (s, v) => s.IsWinoAccountButtonHidden = v, Translator.SettingsAppPreferences_HideWinoAccountButton_Title)));
    }

    private static string BackdropTitle => Translator.MacOS_Personalization_BackdropTitle;
    private static string BackdropDescription => Translator.MacOS_Personalization_BackdropDescription;

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
            var current = themes.FirstOrDefault(theme => theme.Id == (themeService.CurrentApplicationThemeId ?? MacWinoThemeService.DefaultThemeId)) ?? themes.FirstOrDefault();
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _currentThemeName.StringValue = current?.ThemeName ?? string.Empty;
                _currentThemePreview.Show(current);
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }
}

/// <summary>The shared appearance model plus the Mac wallpaper backdrop and window material switches.</summary>
public sealed class MacAppearanceModel : AppearanceSettingsModel
{
    private readonly MacWinoThemeService _themes;

    public MacAppearanceModel(MacWinoThemeService themes) : base(themes)
    {
        _themes = themes;
        MacWinoThemeService.AppearanceChanged += AppearanceChanged;
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

    /// <summary>0 for the default window background, 1 for the translucent material.</summary>
    public int MaterialIndex
    {
        get => _themes.IsTranslucentWindowEnabled ? 1 : 0;
        set
        {
            if (value == MaterialIndex) return;
            _themes.IsTranslucentWindowEnabled = value == 1;
            OnPropertyChanged();
        }
    }

    // The Mac theme service stores the root theme; open windows pick it up when it is applied.
    protected override void OnRootThemeChanged() => _ = _themes.ApplyThemeToActiveWindowAsync();

    protected override void NotifyAppearanceChanged()
    {
        base.NotifyAppearanceChanged();
        OnPropertyChanged(nameof(IsBackdropEnabled));
        OnPropertyChanged(nameof(MaterialIndex));
    }

    private void AppearanceChanged(object? sender, EventArgs args) => NotifyAppearanceChanged();

    public override void Dispose()
    {
        MacWinoThemeService.AppearanceChanged -= AppearanceChanged;
        base.Dispose();
    }
}
