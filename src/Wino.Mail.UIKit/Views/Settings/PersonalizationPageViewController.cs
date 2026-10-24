using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
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
    private readonly UIKitAppearanceModel _appearance = new(themeService);
    private readonly UILabel _currentThemeName = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
    private readonly ThemeThumbnailView _currentThemePreview = new(64, 40);

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;
        Bindings.Own(_appearance);

        var swatches = new WinoColorSwatchPicker { Colors = AppearanceSettingsModel.AccentPalette };
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

        // App modes (Windows PersonalizationPage): the modes the tab bar or sidebar card offers; a mode no account provides stays off.
        AddGroup(null, Expander(Translator.SettingsPersonalization_AppModes_Title, Translator.SettingsPersonalization_AppModes_Description, WinoIconGlyph.Grid, null,
            vm.AppModeOptions.Select(AppModeCard).ToArray()));

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
        => Task.WhenAll(RefreshCurrentThemeAsync(), ViewModel.ActivateAppModeOptionsAsync());

    protected override Task DeactivateAsync()
    {
        ViewModel.DeactivateAppModeOptions();
        return Task.CompletedTask;
    }

    /// <summary>One app mode row: its switch, and the description and enabled state that follow the accounts.</summary>
    private UIView AppModeCard(AppModeOptionViewModel option)
    {
        var card = Card(option.Title, option.Description, option.Icon,
            Bind.Switch(option, nameof(option.IsShown), s => s.IsShown, (s, v) => s.IsShown = v, option.Title));
        Bind.Bind(option, nameof(option.Description), s => s.Description, description => card.Description = description);
        Bind.Bind(option, nameof(option.CanToggle), s => s.CanToggle, canToggle => card.IsEnabled = canToggle);
        return card;
    }

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

/// <summary>The shared appearance model plus the iOS wallpaper backdrop switch.</summary>
public sealed class UIKitAppearanceModel : AppearanceSettingsModel
{
    private readonly UIKitThemeService _themes;

    public UIKitAppearanceModel(UIKitThemeService themes) : base(themes)
    {
        _themes = themes;
        UIKitThemeService.AppearanceChanged += AppearanceChanged;
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

    protected override void NotifyAppearanceChanged()
    {
        base.NotifyAppearanceChanged();
        OnPropertyChanged(nameof(IsBackdropEnabled));
    }

    private void AppearanceChanged(object? sender, EventArgs args) => NotifyAppearanceChanged();

    public override void Dispose()
    {
        UIKitThemeService.AppearanceChanged -= AppearanceChanged;
        base.Dispose();
    }
}
