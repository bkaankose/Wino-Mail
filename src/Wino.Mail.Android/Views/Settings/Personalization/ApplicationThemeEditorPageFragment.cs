using System.Collections.ObjectModel;
using System.ComponentModel;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.Personalization;

/// <summary>
/// Windows ApplicationThemeEditorPage (iOS ApplicationThemeEditorPageViewController): theme name, the palette being
/// edited (Light/Dark) and Copy to the other mode, the wallpaper (Browse or Replace; Fill/Fit and the 3×3 focal point),
/// base presets, the base colour and accent, the surface and calendar hour colours, then Cancel, Delete and Save. Every
/// change previews live through the theme service; leaving without saving restores the theme in use (the route's
/// release runs <see cref="ApplicationThemeEditorPageViewModel.ReleasePreviewAsync"/>), and Back asks the ViewModel first
/// (<see cref="Wino.Core.Domain.Interfaces.IConfirmBackNavigation"/>, honoured by the navigation service).
/// Colour rows show a swatch, the value, a hex field and the colour picker (Android has no system colour well); the
/// Windows surface preview drawings are not drawn here, the live preview behind the page shows the result.
/// </summary>
public sealed class ApplicationThemeEditorPageFragment : SettingsPageFragment<ApplicationThemeEditorPageViewModel>, ISettingsPageTitleSource
{
    private static readonly ThemeWallpaperAlignment[] FocalOrder =
    [
        ThemeWallpaperAlignment.TopLeft, ThemeWallpaperAlignment.Top, ThemeWallpaperAlignment.TopRight,
        ThemeWallpaperAlignment.Left, ThemeWallpaperAlignment.Center, ThemeWallpaperAlignment.Right,
        ThemeWallpaperAlignment.BottomLeft, ThemeWallpaperAlignment.Bottom, ThemeWallpaperAlignment.BottomRight
    ];

    private readonly RadioButton[] _focal = new RadioButton[9];
    private ImageView? _wallpaperPreview;
    private long _wallpaperRevision;

    public string? PageTitle => ViewModel?.PageTitle;

    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = VM;

        var error = InfoBar(WinoInfoBarSeverity.Error, null, null);
        error.IsClosable = true;
        EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => vm.IsErrorOpen = false;
        error.Closed += closed;
        Bindings.Own(new ActionDisposable(() => error.Closed -= closed));
        Bind.Bind(vm, nameof(vm.ErrorMessage), s => s.ErrorMessage, message => error.Message = message);
        Bind.Visible(error, vm, nameof(vm.IsErrorOpen), s => s.IsErrorOpen);
        Add(error);

        var name = Bind.TextField(vm, nameof(vm.ThemeName), s => s.ThemeName, (s, value) => s.ThemeName = value, Translator.ApplicationThemeEditor_Name);
        var paletteMode = Bind.Segmented(vm, [Translator.ApplicationThemeGallery_Light, Translator.ApplicationThemeGallery_Dark],
            nameof(vm.PaletteModeIndex), s => s.PaletteModeIndex, (s, index) => s.SelectPaletteMode(index));
        paletteMode.ContentDescription = Translator.ApplicationThemeEditor_EditingPalette;
        void MarkCustomized(int index, bool customized)
        {
            if (paletteMode.GetChildAt(index) is Button button)
            {
                var title = index == 0 ? Translator.ApplicationThemeGallery_Light : Translator.ApplicationThemeGallery_Dark;
                button.Text = customized ? $"{title} •" : title;
            }
        }
        Bind.Bind(vm, nameof(vm.HasLightOverrides), s => s.HasLightOverrides, value => MarkCustomized(0, value));
        Bind.Bind(vm, nameof(vm.HasDarkOverrides), s => s.HasDarkOverrides, value => MarkCustomized(1, value));
        var copy = Bind.Button(string.Empty, vm.CopyPaletteToOtherModeCommand);
        Bind.Bind(vm, nameof(vm.CopyPaletteLabel), s => s.CopyPaletteLabel, label => copy.Text = label ?? string.Empty);
        var paletteContent = Stack(Resource.Dimension.wino_space_s, paletteMode, copy);
        Wrap(paletteMode);
        Wrap(copy);

        AddGroup(null,
            StackedCard(Translator.ApplicationThemeEditor_Name, Translator.ApplicationThemeEditor_NameDescription, WinoIconGlyph.Rename, name),
            StackedCard(Translator.ApplicationThemeEditor_EditingPalette, Translator.ApplicationThemeEditor_PaletteHint, WinoIconGlyph.DarkTheme, paletteContent));

        Add(BuildWallpaperExpander());
        Add(BuildBaseAndAccentExpander());

        var surfaceRows = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        var surfaces = Expander(Translator.ApplicationThemeEditor_Surfaces, Translator.ApplicationThemeEditor_SurfacesDescription, WinoIconGlyph.Grid, null);
        surfaces.Add(surfaceRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenSurfaceCount), s => s.OverriddenSurfaceCount,
            count => surfaces.HeaderCard.Description = DescribeCount(Translator.ApplicationThemeEditor_SurfacesDescription, count));
        Add(surfaces);

        var calendarRows = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        var calendar = Expander(Translator.ApplicationThemeEditor_CalendarHours, Translator.ApplicationThemeEditor_CalendarHoursDescription, WinoIconGlyph.Calendar, null);
        calendar.Add(calendarRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenCalendarCount), s => s.OverriddenCalendarCount, _ => calendar.HeaderCard.Description =
            DescribeCount(Translator.ApplicationThemeEditor_CalendarHoursDescription, vm.CalendarOptions.Count(option => IsShown(option.Key) && option.IsOverridden)));
        Add(calendar);

        BindRows(vm.SurfaceOptions, surfaceRows);
        BindRows(vm.CalendarOptions, calendarRows);

        // Footer: Cancel, Delete (edit mode) and Save.
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        var delete = Bind.Button(Translator.Buttons_Delete, vm.DeleteCommand);
        delete.SetTextColor(WinoColors.Critical(Ctx));
        Bind.Visible(delete, vm, nameof(vm.IsEditMode), s => s.IsEditMode);
        var save = Bind.Button(Translator.Buttons_Save, () => { if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null); }, primary: true);
        Bind.Views.BindAny(vm, s => !s.IsSaving && !s.IsDeleting, enabled => save.Enabled = enabled);
        var footer = Row(cancel, delete, save);
        footer.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);
        footer.SetPadding(0, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_m), 0, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l));
        Add(footer);

        Bind.Bind(vm, nameof(vm.WallpaperFit), s => s.WallpaperFit, _ => UpdateWallpaperPreview());
        Bind.Bind(vm, nameof(vm.WallpaperAlignment), s => s.WallpaperAlignment, _ => UpdateFocalButtons());
        Bind.Bind(vm, nameof(vm.WallpaperPreviewPath), s => s.WallpaperPreviewPath, path => _ = LoadWallpaperAsync(path));
        Bind.Bind(vm, nameof(vm.PageTitle), s => s.PageTitle, _ => UpdateTitle());
    }

    private static void Wrap(View view)
    {
        if (view.LayoutParameters is LinearLayout.LayoutParams layout) layout.Width = ViewGroup.LayoutParams.WrapContent;
    }

    private static string DescribeCount(string description, int count)
        => count > 0 ? $"{description} {string.Format(Translator.ApplicationThemeEditor_CustomizedCountFormat, count)}" : description;

    /// <summary>
    /// The Android calendar grid has no hover or selected slot fill, so those two colours are not offered; their saved
    /// values round-trip untouched (as on iOS and the Mac).
    /// </summary>
    private static bool IsShown(CustomThemeColorKey key)
        => key is not (CustomThemeColorKey.CalendarHoverHour or CustomThemeColorKey.CalendarSelectedHour);

    // ---- Colour rows ----

    private void BindRows(ObservableCollection<ThemePaletteColorOptionViewModel> options, LinearLayout host)
    {
        BindingScope? rows = null;
        Bind.Collection(options, () =>
        {
            rows?.Dispose();
            rows = new BindingScope();
            host.RemoveAllViews();
            foreach (var option in options.Where(option => IsShown(option.Key)).ToList())
            {
                if (host.ChildCount > 0)
                {
                    var divider = new View(Ctx);
                    divider.SetBackgroundColor(WinoColors.Divider(Ctx));
                    host.AddView(divider, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)));
                }
                host.AddView(BuildOptionRow(option, rows), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            }
        });
        Bindings.Own(new ActionDisposable(() => rows?.Dispose()));
    }

    /// <summary>One editable colour: swatch, label and value; tapping opens the hex field, the picker, Reset and the contrast note.</summary>
    private View BuildOptionRow(ThemePaletteColorOptionViewModel option, BindingScope scope)
    {
        var swatch = new CheckerSwatch(Ctx);
        swatch.LayoutParameters = new ViewGroup.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_swatch), WinoTokens.Px(Ctx, Resource.Dimension.wino_size_swatch));
        var value = Caption(string.Empty);
        value.Typeface = Typeface.Monospace;
        var dot = new View(Ctx) { ContentDescription = Translator.ApplicationThemeEditor_Customized };
        var dotFill = new global::Android.Graphics.Drawables.GradientDrawable();
        dotFill.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
        dotFill.SetColor(WinoColors.Accent(Ctx));
        dot.Background = dotFill;
        dot.LayoutParameters = new ViewGroup.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xs), WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xs));
        var card = new WinoSettingsCard(Ctx, option.Label ?? string.Empty, option.Description) { IsNested = true, IsClickable = true, LeadingView = swatch, Content = Row(dot, value) };

        var well = new ThemeColorWell(Ctx, allowsAlpha: true, option.Label ?? string.Empty);
        EventHandler<string> picked = (_, hex) => { if (!string.Equals(option.Value, hex, StringComparison.OrdinalIgnoreCase)) option.Value = hex; };
        well.ColorPicked += picked;
        var hexLayout = SettingsBinder.CreateTextField(Ctx, Translator.ApplicationThemeEditor_HexFormatWithAlpha);
        var hexField = hexLayout.EditText;
        if (hexField is not null)
        {
            hexField.Typeface = Typeface.Monospace;
            hexField.ImeOptions = global::Android.Views.InputMethods.ImeAction.Done;
            hexField.FocusChange += (_, args) => { if (!args.HasFocus) CommitHex(option, hexField); };
            hexField.EditorAction += (_, args) => { CommitHex(option, hexField); args.Handled = false; };
        }
        var reset = SettingsBinder.CreateButton(Ctx, Translator.ApplicationThemeEditor_ResetToDefault);
        reset.Click += (_, _) => { if (VM.ResetColorCommand.CanExecute(option)) VM.ResetColorCommand.Execute(option); };
        var hint = Caption(Translator.ApplicationThemeEditor_AlphaHint);
        var contrast = Caption(string.Empty);
        var controls = Row(well, hexLayout);
        ((LinearLayout.LayoutParams)hexLayout.LayoutParameters!).Width = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_field);
        var panel = Stack(Resource.Dimension.wino_space_m, controls, reset, hint, contrast);
        Wrap(reset);

        EventHandler activated = (_, _) => { if (VM.ToggleOptionCommand.CanExecute(option)) VM.ToggleOptionCommand.Execute(option); };
        card.Activated += activated;

        void Refresh()
        {
            swatch.Color = WinoColors.FromHexString(option.Value);
            value.Text = option.Value ?? string.Empty;
            dot.Visibility = option.IsOverridden ? ViewStates.Visible : ViewStates.Gone;
            well.SetHex(option.Value);
            if (hexField is not null && !hexField.HasFocus) hexField.Text = option.Value ?? string.Empty;
            contrast.Visibility = option.HasContrastReport ? ViewStates.Visible : ViewStates.Gone;
            contrast.Text = option.ContrastMessage ?? string.Empty;
            contrast.SetTextColor(option.IsContrastSufficient ? WinoColors.Success(Ctx) : WinoColors.Caution(Ctx));
            card.SetActionIcon(option.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight);
            card.BottomContent = option.IsExpanded ? panel : null;
        }

        PropertyChangedEventHandler changed = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => { if (!scope.IsDisposed) Refresh(); });
        option.PropertyChanged += changed;
        scope.Own(new ActionDisposable(() =>
        {
            option.PropertyChanged -= changed;
            card.Activated -= activated;
            well.ColorPicked -= picked;
        }));
        Refresh();
        return card;
    }

    private static void CommitHex(ThemePaletteColorOptionViewModel option, EditText field)
    {
        var text = (field.Text ?? string.Empty).Trim();
        if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
        if (text.Length == 0 || !ThemeColorValidator.IsValidSurface(text))
        {
            field.Text = option.Value ?? string.Empty;
            return;
        }
        if (!string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase)) option.Value = text.ToUpperInvariant();
    }

    // ---- Wallpaper ----

    private WinoSettingsExpander BuildWallpaperExpander()
    {
        var vm = VM;
        var browse = Bind.Button(Translator.Buttons_Browse, vm.ChooseWallpaperCommand);
        var emptyText = Text(Translator.ApplicationThemeEditor_WallpaperNone, WinoTextStyle.BodySecondary);
        var empty = Row(emptyText, browse);
        Bind.Visible(empty, vm, nameof(vm.HasWallpaper), s => !s.HasWallpaper);

        _wallpaperPreview = new ImageView(Ctx) { ContentDescription = Translator.ApplicationThemeEditor_FocalPoint };
        _wallpaperPreview.SetScaleType(ImageView.ScaleType.CenterCrop);
        _wallpaperPreview.SetBackgroundColor(WinoColors.SubtleFill(Ctx));
        _wallpaperPreview.ClipToOutline = true;
        var previewLayout = new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_wallpaper_preview_width), WinoTokens.Px(Ctx, Resource.Dimension.wino_wallpaper_preview_height));

        // The focal point: a 3×3 grid of radio buttons (the Mac radio buttons over the preview).
        var focalGrid = new GridLayout(Ctx) { ColumnCount = 3 };
        for (int index = 0; index < 9; index++)
        {
            var alignment = FocalOrder[index];
            var radio = new RadioButton(Ctx) { ContentDescription = FocalName(alignment) };
            WinoMaterialAccent.Tint(radio);
            radio.Click += (_, _) =>
            {
                if (vm.SelectFocalPointCommand.CanExecute(alignment)) vm.SelectFocalPointCommand.Execute(alignment);
                UpdateFocalButtons();
            };
            _focal[index] = radio;
            focalGrid.AddView(radio);
        }
        Bind.Bind(vm, nameof(vm.IsFocalSelectorEnabled), s => s.IsFocalSelectorEnabled, enabled =>
        {
            foreach (var radio in _focal) radio.Enabled = enabled;
        });
        var focalDescription = Bind.Label(vm, nameof(vm.FocalPointDescription), s => s.FocalPointDescription, maximumLines: 0);

        var fit = Bind.Segmented(vm, [Translator.ApplicationThemeEditor_Fill, Translator.ApplicationThemeEditor_Fit],
            nameof(vm.WallpaperFitIndex), s => s.WallpaperFitIndex, (s, index) => s.SelectWallpaperFit(index));
        fit.ContentDescription = Translator.ApplicationThemeEditor_WallpaperFit;
        var fitRow = Row(Text(Translator.ApplicationThemeEditor_WallpaperFit), fit);

        var fileName = Bind.Label(vm, nameof(vm.WallpaperFileName), s => s.WallpaperFileName);
        fileName.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
        var replace = Bind.Button(Translator.ApplicationThemeEditor_WallpaperReplace, vm.ChooseWallpaperCommand);
        var replaceRow = Row(fileName, replace);

        var image = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        image.AddView(_wallpaperPreview, previewLayout);
        AddTo(image, Row(focalGrid, focalDescription), Resource.Dimension.wino_space_m);
        AddTo(image, fitRow, Resource.Dimension.wino_space_m);
        AddTo(image, replaceRow, Resource.Dimension.wino_space_m);
        Bind.Visible(image, vm, nameof(vm.HasWallpaper), s => s.HasWallpaper);

        var content = Stack(0, empty, image);
        var expander = Expander(Translator.ApplicationThemeEditor_Wallpaper, Translator.ApplicationThemeEditor_WallpaperDescription, WinoIconGlyph.Image, null);
        AddNested(expander, content, Resource.Dimension.wino_space_m, 0, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_l);
        expander.IsExpanded = true;
        return expander;
    }

    private static string FocalName(ThemeWallpaperAlignment alignment) => alignment switch
    {
        ThemeWallpaperAlignment.TopLeft => Translator.ApplicationThemeEditor_FocalTopLeft,
        ThemeWallpaperAlignment.Top => Translator.ApplicationThemeEditor_FocalTop,
        ThemeWallpaperAlignment.TopRight => Translator.ApplicationThemeEditor_FocalTopRight,
        ThemeWallpaperAlignment.Left => Translator.ApplicationThemeEditor_FocalLeft,
        ThemeWallpaperAlignment.Right => Translator.ApplicationThemeEditor_FocalRight,
        ThemeWallpaperAlignment.BottomLeft => Translator.ApplicationThemeEditor_FocalBottomLeft,
        ThemeWallpaperAlignment.Bottom => Translator.ApplicationThemeEditor_FocalBottom,
        ThemeWallpaperAlignment.BottomRight => Translator.ApplicationThemeEditor_FocalBottomRight,
        _ => Translator.ApplicationThemeEditor_FocalCenter
    };

    private void UpdateFocalButtons()
    {
        if (ViewModel is not { } vm) return;
        for (int index = 0; index < _focal.Length; index++)
            if (_focal[index] is { } radio) radio.Checked = FocalOrder[index] == vm.WallpaperAlignment;
    }

    private void UpdateWallpaperPreview()
    {
        if (_wallpaperPreview is null || ViewModel is not { } vm) return;
        _wallpaperPreview.SetScaleType(vm.WallpaperFit == ThemeWallpaperFit.Fit ? ImageView.ScaleType.FitCenter : ImageView.ScaleType.CenterCrop);
    }

    /// <summary>Decodes the wallpaper once per change, in the background, for the preview and the contrast average.</summary>
    private async Task LoadWallpaperAsync(string? path)
    {
        var revision = ++_wallpaperRevision;
        string? localPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : path;
        Bitmap? image = null;
        var average = string.Empty;
        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
        {
            (image, average) = await Task.Run(() =>
            {
                try
                {
                    var decoded = BitmapFactory.DecodeFile(localPath);
                    return (decoded, AverageColorHex(decoded));
                }
                catch (Exception exception)
                {
                    Serilog.Log.Warning(exception, "Could not read the theme wallpaper for its preview.");
                    return ((Bitmap?)null, string.Empty);
                }
            });
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed || revision != _wallpaperRevision || ViewModel is not { } vm) return;
            _wallpaperPreview?.SetImageBitmap(image);
            vm.WallpaperAverageColor = average;
            UpdateWallpaperPreview();
        });
    }

    /// <summary>The average colour of the image as #RRGGBB (the Windows editor's 8×8 average), or empty.</summary>
    private static string AverageColorHex(Bitmap? image)
    {
        if (image is null) return string.Empty;
        using var small = Bitmap.CreateScaledBitmap(image, 8, 8, true);
        long red = 0, green = 0, blue = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var pixel = new Color(small.GetPixel(x, y));
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
            }
        return $"#{red / 64:X2}{green / 64:X2}{blue / 64:X2}";
    }

    // ---- Base surfaces and accent ----

    private WinoSettingsExpander BuildBaseAndAccentExpander()
    {
        var vm = VM;
        var presets = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        foreach (var preset in vm.BasePresets)
        {
            var swatch = new PresetSwatch(Ctx, WinoColors.FromHexString(preset.LightColor) ?? WinoTokens.Color(Ctx, Resource.Color.wino_swatch_fallback), WinoColors.FromHexString(preset.DarkColor) ?? WinoTokens.Color(Ctx, Resource.Color.wino_swatch_fallback))
            {
                ContentDescription = preset.Name,
                Clickable = true,
                Focusable = true
            };
            swatch.Click += (_, _) => vm.ApplyBasePresetCommand.Execute(preset);
            var layout = new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_control), WinoTokens.Px(Ctx, Resource.Dimension.wino_size_swatch)) { MarginEnd = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) };
            presets.AddView(swatch, layout);
        }
        var presetScroll = new HorizontalScrollView(Ctx) { HorizontalScrollBarEnabled = false };
        presetScroll.AddView(presets);
        var presetCard = StackedCard(Translator.ApplicationThemeEditor_Presets, null, WinoIconGlyph.None, presetScroll);

        var baseRows = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        BindRows(vm.BaseSurfaceOptions, baseRows);

        var useSystem = Bind.Switch(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, (s, value) => s.UseSystemAccent = value, Translator.ApplicationThemeEditor_UseSystemAccentColor);
        var systemCard = Card(Translator.ApplicationThemeEditor_UseSystemAccentColor, null, WinoIconGlyph.None, useSystem);

        var accentWell = new ThemeColorWell(Ctx, allowsAlpha: false, Translator.ApplicationThemeEditor_Accent);
        EventHandler<string> accentPicked = (_, hex) => { if (!string.Equals(vm.AccentColorHex, hex, StringComparison.OrdinalIgnoreCase)) vm.AccentColorHex = hex; };
        accentWell.ColorPicked += accentPicked;
        Bindings.Own(new ActionDisposable(() => accentWell.ColorPicked -= accentPicked));
        var accentHex = Bind.TextField(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, (s, value) => s.AccentColorHex = value, Translator.ApplicationThemeEditor_HexFormat);
        Bind.Bind(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, hex => accentWell.SetHex(hex));
        var accentControls = Row(accentWell, accentHex);
        ((LinearLayout.LayoutParams)accentHex.LayoutParameters!).Width = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_field_narrow);
        var accentCard = StackedCard(Translator.ApplicationThemeEditor_CustomAccent, null, WinoIconGlyph.None, accentControls);
        Bind.Enabled(accentCard, vm, nameof(vm.UseSystemAccent), s => !s.UseSystemAccent);

        var expander = Expander(Translator.ApplicationThemeEditor_BaseAndAccent, Translator.ApplicationThemeEditor_BaseAndAccentDescription, WinoIconGlyph.Color, null);
        expander.Add(presetCard);
        expander.Add(baseRows, 0, 0, 0, 0);
        expander.Add(systemCard);
        expander.Add(accentCard);
        return expander;
    }

    /// <summary>A preset's swatch: half its light colour, half its dark colour.</summary>
    private sealed class PresetSwatch(global::Android.Content.Context context, Color light, Color dark) : View(context)
    {
        private readonly Paint _paint = new(PaintFlags.AntiAlias);

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float radius = WinoTokens.PxF(this.RequireContext(), Resource.Dimension.wino_radius_small);
            using var clip = new global::Android.Graphics.Path();
            clip.AddRoundRect(0, 0, Width, Height, radius, radius, global::Android.Graphics.Path.Direction.Cw!);
            canvas.Save();
            canvas.ClipPath(clip);
            _paint.Color = light;
            canvas.DrawRect(0, 0, Width / 2f, Height, _paint);
            _paint.Color = dark;
            canvas.DrawRect(Width / 2f, 0, Width, Height, _paint);
            canvas.Restore();
        }
    }
}
