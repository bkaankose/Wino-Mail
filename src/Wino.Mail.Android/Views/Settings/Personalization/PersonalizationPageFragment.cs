using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.Personalization;

/// <summary>
/// Personalization (Windows PersonalizationPage, iOS PersonalizationPageViewController): the accent (the Windows palette
/// or the system accent), light, dark or system appearance, the icon style, the Wino theme gallery entry with the current
/// theme, the wallpaper backdrop switch and the account menu options. Android has no window material, so the Windows
/// backdrop material card is absent.
/// </summary>
/// <remarks>
/// As on iOS and the Mac, the shared ViewModel's navigation work drives the Windows pickers, so the route does not run
/// OnNavigatedTo: this page reads and writes the theme service through <see cref="AndroidAppearanceModel"/> and keeps the
/// ViewModel for preferences and commands.
/// </remarks>
public sealed class PersonalizationPageFragment : SettingsPageFragment<PersonalizationPageViewModel>
{
    // The Windows accent palette (PersonalizationPageViewModel.InitializeColors).
    private static readonly string[] AccentPalette =
    [
        "#0078d7", "#00838c", "#e3008c", "#ca4f07", "#e81123", "#00819e", "#10893e", "#881798",
        "#c239b3", "#767676", "#e1b12c", "#16a085", "#0984e3", "#4a69bd", "#05c46b"
    ];

    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;
        if (MainApplication.Services?.GetService<AndroidThemeService>() is not { } themes) return;
        var appearance = Bindings.Own(new AndroidAppearanceModel(themes));

        var swatches = new AccentSwatchRow(Ctx, AccentPalette);
        Bind.Bind(appearance, nameof(AndroidAppearanceModel.AccentHex), s => s.AccentHex, hex => swatches.SelectedHex = hex);
        swatches.Picked += (_, hex) => appearance.AccentHex = hex;
        Bind.Enabled(swatches, appearance, nameof(AndroidAppearanceModel.UseSystemAccent), s => !s.UseSystemAccent);

        var accent = Expander(Translator.SettingsAccentColor_Title, Translator.SettingsAccentColor_Description, WinoIconGlyph.Color, null);
        AddNested(accent, swatches, Resource.Dimension.wino_space_m, 0, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_l);
        AddNested(accent, Bind.Checkbox(appearance, Translator.SettingsAccentColor_UseSystemAccentColor, nameof(AndroidAppearanceModel.UseSystemAccent),
            s => s.UseSystemAccent, (s, v) => s.UseSystemAccent = v), Resource.Dimension.wino_space_2xs, 0, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_l);

        var appearanceCard = StackedCard(Translator.SettingsElementTheme_Title, Translator.SettingsElementTheme_SelectThemeDescription, WinoIconGlyph.DarkTheme,
            // Material 3 segmented button with the short Android labels, in the Windows order (system, light, dark).
            Bind.Segmented(appearance, [Translator.Android_Appearance_System, Translator.Android_Appearance_Light, Translator.Android_Appearance_Dark],
                nameof(AndroidAppearanceModel.ThemeIndex), s => s.ThemeIndex, (s, v) => s.ThemeIndex = v));

        var iconStyle = StackedCard(Translator.SettingsIconStyle_Title, Translator.SettingsIconStyle_Description, WinoIconGlyph.IconStyle,
            Bind.Segmented(p, vm.IconStyles.Select(style => style.Title).ToArray(), nameof(p.IconStyle),
                s => vm.IconStyles.FindIndex(style => style.IconStyle == s.IconStyle),
                (s, v) => { if (v >= 0 && v < vm.IconStyles.Count) s.IconStyle = vm.IconStyles[v].IconStyle; }));

        AddGroup(null, accent, appearanceCard, iconStyle);

        // Wino themes: one entry point into the theme gallery, like the Windows Personalization page.
        var currentName = Caption(string.Empty);
        var currentPreview = new ThemeThumbnailView(Ctx, Resource.Dimension.wino_theme_thumb_width, Resource.Dimension.wino_theme_thumb_height);
        AddGroup(null, NavigationCard(Translator.ApplicationThemeGallery_Title, Translator.ApplicationThemeGallery_Description, WinoIconGlyph.PaintBrush,
            () => vm.NavigateApplicationThemesCommand.Execute(null), Row(currentName, currentPreview)));
        async void Refresh()
        {
            try
            {
                var all = await themes.GetAvailableThemesAsync();
                var currentId = themes.CurrentApplicationThemeId ?? WinoThemeCatalog.DefaultThemeId;
                var current = all.FirstOrDefault(theme => theme.Id == currentId) ?? all.FirstOrDefault();
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    if (Bindings.IsDisposed) return;
                    currentName.Text = current?.ThemeName ?? string.Empty;
                    currentPreview.Show(current);
                });
            }
            catch (Exception exception) { ReportError(exception); }
        }
        Refresh();

        var backdrop = Card(Translator.MacOS_Personalization_BackdropTitle, Translator.IOS_Personalization_BackdropDescription, WinoIconGlyph.Image,
            Bind.Switch(appearance, nameof(AndroidAppearanceModel.IsBackdropEnabled), s => s.IsBackdropEnabled, (s, v) => s.IsBackdropEnabled = v,
                Translator.MacOS_Personalization_BackdropTitle));

        AddGroup(null,
            backdrop,
            Card(Translator.SettingsCompactAccountMenuItem_Title, Translator.SettingsCompactAccountMenuItem_Description, WinoIconGlyph.List,
                Bind.Switch(p, nameof(p.IsCompactAccountMenuItemEnabled), s => s.IsCompactAccountMenuItemEnabled, (s, v) => s.IsCompactAccountMenuItemEnabled = v, Translator.SettingsCompactAccountMenuItem_Title)),
            Card(Translator.SettingsAppPreferences_HideWinoAccountButton_Title, Translator.SettingsAppPreferences_HideWinoAccountButton_Description, WinoIconGlyph.Person,
                Bind.Switch(p, nameof(p.IsWinoAccountButtonHidden), s => s.IsWinoAccountButtonHidden, (s, v) => s.IsWinoAccountButtonHidden = v, Translator.SettingsAppPreferences_HideWinoAccountButton_Title)));
    }

    /// <summary>
    /// A wrapping row of round accent swatches (canvas ThemeGallery "Accent"; iOS WinoColorSwatchPicker): 40 dp circles
    /// 12 dp apart, the selected one in a 2 dp on-surface ring.
    /// </summary>
    private sealed class AccentSwatchRow : ViewGroup
    {
        private readonly List<(string Hex, View View, GradientDrawable Ring)> _items = [];
        private readonly int _size;
        private readonly int _gap;
        private string? _selected;

        public AccentSwatchRow(Context context, IReadOnlyList<string> colors) : base(context)
        {
            _size = WinoTokens.Px(context, Resource.Dimension.wino_m3_swatch);
            _gap = WinoTokens.Px(context, Resource.Dimension.wino_m3_swatch_gap);
            foreach (var hex in colors)
            {
                var fill = new GradientDrawable();
                fill.SetShape(ShapeType.Oval);
                fill.SetColor(WinoColors.FromHexString(hex) ?? WinoTokens.Color(context, Resource.Color.wino_swatch_fallback));
                var ring = new GradientDrawable();
                ring.SetShape(ShapeType.Oval);
                ring.SetColor(Color.Transparent);
                var swatch = new View(context) { Background = fill, Foreground = ring, Clickable = true, Focusable = true };
                swatch.ContentDescription = string.Format(System.Globalization.CultureInfo.CurrentCulture, Translator.Accessibility_ColorOption, hex);
                swatch.Click += (_, _) =>
                {
                    if (!Enabled) return;
                    SelectedHex = hex;
                    Picked?.Invoke(this, hex);
                };
                AddView(swatch, new LayoutParams(_size, _size));
                _items.Add((hex, swatch, ring));
            }
        }

        public event EventHandler<string>? Picked;

        private int Columns(int width) => Math.Max(1, (width + _gap) / (_size + _gap));

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int width = MeasureSpec.GetSize(widthMeasureSpec);
            var cell = MeasureSpec.MakeMeasureSpec(_size, MeasureSpecMode.Exactly);
            for (int index = 0; index < ChildCount; index++) GetChildAt(index)?.Measure(cell, cell);
            int rows = (ChildCount + Columns(width) - 1) / Columns(width);
            SetMeasuredDimension(width, rows == 0 ? 0 : rows * _size + (rows - 1) * _gap);
        }

        protected override void OnLayout(bool changed, int l, int t, int r, int b)
        {
            int columns = Columns(r - l);
            bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
            for (int index = 0; index < ChildCount; index++)
            {
                int x = (index % columns) * (_size + _gap);
                int y = (index / columns) * (_size + _gap);
                if (rtl) x = r - l - x - _size;
                GetChildAt(index)?.Layout(x, y, x + _size, y + _size);
            }
        }

        public string? SelectedHex
        {
            get => _selected;
            set
            {
                _selected = value;
                var context = this.RequireContext();
                foreach (var (hex, view, ring) in _items)
                {
                    bool selected = string.Equals(hex, value, StringComparison.OrdinalIgnoreCase);
                    ring.SetStroke(selected ? WinoTokens.Px(context, Resource.Dimension.wino_m3_swatch_ring) : 0, WinoColors.PrimaryText(context));
                    view.Selected = selected;
                }
            }
        }

        public override bool Enabled
        {
            get => base.Enabled;
            set
            {
                base.Enabled = value;
                Alpha = value ? 1f : WinoColors.Fraction(this.RequireContext(), Resource.Fraction.wino_m3_disabled_alpha);
            }
        }
    }
}
