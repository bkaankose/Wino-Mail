using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>The wallpaper and the base surface and accent sections of the theme editor.</summary>
public sealed partial class ApplicationThemeEditorPageViewController
{
    private const double WallpaperAspect = 200.0 / 320.0;

    private WinoSettingsExpander BuildWallpaperExpander()
    {
        var vm = ViewModel;

        // No image yet: Browse (the dialog service picks a JPEG or PNG from Photos or Files).
        var browse = Bind.Button(Translator.Buttons_Browse, vm.ChooseWallpaperCommand);
        browse.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        var emptyText = WinoStyle.Label(Translator.ApplicationThemeEditor_WallpaperNone, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var empty = WinoLayout.HStack(WinoStyle.Space3, emptyText, WinoLayout.Spacer(), browse);
        empty.Alignment = UIStackViewAlignment.Center;
        Bind.Visible(empty, vm, nameof(vm.HasWallpaper), s => !s.HasWallpaper);

        // An image: the preview with the focal point grid, then Fill/Fit, the file name and Replace under it.
        WinoAccessibility.Label(_wallpaperPreview, Translator.ApplicationThemeEditor_FocalPoint);
        var preferredWidth = _wallpaperPreview.WidthAnchor.ConstraintEqualTo(320);
        preferredWidth.Priority = 750;
        preferredWidth.Active = true;
        _wallpaperPreview.HeightAnchor.ConstraintEqualTo(_wallpaperPreview.WidthAnchor, (nfloat)WallpaperAspect).Active = true;

        var focalRows = new UIView[3];
        for (int row = 0; row < 3; row++)
        {
            var cells = new UIView[3];
            for (int column = 0; column < 3; column++)
            {
                int index = row * 3 + column;
                var alignment = FocalOrder[index];
                var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, TintColor = UIColor.White };
                button.Layer.ShadowColor = UIColor.Black.CGColor;
                button.Layer.ShadowOpacity = 0.45f;
                button.Layer.ShadowRadius = 2;
                button.Layer.ShadowOffset = new CGSize(0, 0.5);
                WinoAccessibility.Label(button, FocalName(alignment));
                Bind.OnActivated(button, () =>
                {
                    if (vm.SelectFocalPointCommand.CanExecute(alignment)) vm.SelectFocalPointCommand.Execute(alignment);
                    UpdateFocalButtons();
                });
                _focalButtons[index] = button;
                cells[column] = button;
            }
            var line = WinoLayout.HStack(0, cells);
            line.Distribution = UIStackViewDistribution.FillEqually;
            line.Alignment = UIStackViewAlignment.Fill;
            focalRows[row] = line;
        }
        var focalGrid = WinoLayout.VStack(0, focalRows);
        focalGrid.Distribution = UIStackViewDistribution.FillEqually;
        focalGrid.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(focalGrid, _wallpaperPreview);
        Bind.Bind(vm, nameof(vm.IsFocalSelectorEnabled), s => s.IsFocalSelectorEnabled, enabled =>
        {
            foreach (var button in _focalButtons) button.Hidden = !enabled;
        });

        _wallpaperFit.InsertSegment(Translator.ApplicationThemeEditor_Fill, 0, false);
        _wallpaperFit.InsertSegment(Translator.ApplicationThemeEditor_Fit, 1, false);
        WinoAccessibility.Label(_wallpaperFit, Translator.ApplicationThemeEditor_WallpaperFit);
        Bind.OnActivated(_wallpaperFit, () => vm.SelectWallpaperFit((int)_wallpaperFit.SelectedSegment), UIControlEvent.ValueChanged);
        Bind.Bind(vm, nameof(vm.WallpaperFitIndex), s => s.WallpaperFitIndex, index => _wallpaperFit.SelectedSegment = index);
        var fitRow = WinoLayout.HStack(WinoStyle.Space2, WinoStyle.Label(Translator.ApplicationThemeEditor_WallpaperFit, WinoStyle.Body), _wallpaperFit);
        fitRow.Alignment = UIStackViewAlignment.Center;

        var focalDescription = Bind.Label(vm, nameof(vm.FocalPointDescription), s => s.FocalPointDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var fileName = Bind.Label(vm, nameof(vm.WallpaperFileName), s => s.WallpaperFileName, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        fileName.LineBreakMode = UILineBreakMode.MiddleTruncation;
        var replace = Bind.Button(Translator.ApplicationThemeEditor_WallpaperReplace, vm.ChooseWallpaperCommand);
        replace.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        WinoLayout.Priorities(replace, UILayoutConstraintAxis.Horizontal, 750, 1000);
        var replaceRow = WinoLayout.HStack(WinoStyle.Space2, fileName, replace);
        replaceRow.Alignment = UIStackViewAlignment.Center;

        var image = WinoLayout.VStack(WinoStyle.Space3, _wallpaperPreview, focalDescription, fitRow, replaceRow);
        image.Alignment = UIStackViewAlignment.Leading;
        _wallpaperPreview.WidthAnchor.ConstraintLessThanOrEqualTo(image.WidthAnchor).Active = true;
        replaceRow.WidthAnchor.ConstraintLessThanOrEqualTo(image.WidthAnchor).Active = true;
        Bind.Visible(image, vm, nameof(vm.HasWallpaper), s => s.HasWallpaper);

        var content = WinoLayout.VStack(0, empty, image);
        content.Alignment = UIStackViewAlignment.Fill;
        var expander = Expander(Translator.ApplicationThemeEditor_Wallpaper, Translator.ApplicationThemeEditor_WallpaperDescription, WinoIconGlyph.Image, null);
        expander.Add(content, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
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

    /// <summary>A filled ring on the focal point in use, an open ring elsewhere (the Mac radio buttons).</summary>
    private void UpdateFocalButtons()
    {
        for (int index = 0; index < _focalButtons.Length; index++)
        {
            if (_focalButtons[index] is not { } button) continue;
            bool selected = FocalOrder[index] == ViewModel.WallpaperAlignment;
            var symbol = selected ? "largecircle.fill.circle" : "circle";
            button.SetImage(UIImage.GetSystemImage(symbol, UIImageSymbolConfiguration.Create(UIFontTextStyle.Title3)), UIControlState.Normal);
            button.Selected = false;
            button.AccessibilityTraits = selected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }

    private WinoSettingsExpander BuildBaseAndAccentExpander()
    {
        var vm = ViewModel;

        // Presets: one 44pt button per preset, its swatch half light and half dark.
        var presets = WinoLayout.HStack(WinoStyle.Space2);
        foreach (var preset in vm.BasePresets)
        {
            var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
            var configuration = UIButtonConfiguration.GrayButtonConfiguration;
            configuration.Image = PresetSwatch(preset).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
            configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 6, 6, 6);
            button.Configuration = configuration;
            WinoLayout.Size(button, 44, 44);
            WinoAccessibility.Label(button, preset.Name);
            Bind.OnActivated(button, () => vm.ApplyBasePresetCommand.Execute(preset));
            presets.AddArrangedSubview(button);
        }
        var presetCard = StackedCard(Translator.ApplicationThemeEditor_Presets, null, WinoIconGlyph.None, presets);

        var useSystem = Bind.Switch(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, (s, value) => s.UseSystemAccent = value, Translator.ApplicationThemeEditor_UseSystemAccentColor);
        var systemCard = Card(Translator.ApplicationThemeEditor_UseSystemAccentColor, null, WinoIconGlyph.None, useSystem);

        var accentWell = new ThemeColorWell(allowsAlpha: false, Translator.ApplicationThemeEditor_Accent);
        _staticWells.Add(accentWell);
        EventHandler<string> accentPicked = (_, hex) =>
        {
            if (!string.Equals(vm.AccentColorHex, hex, StringComparison.OrdinalIgnoreCase)) vm.AccentColorHex = hex;
        };
        accentWell.ColorPicked += accentPicked;
        Bindings.Own(new ActionDisposable(() => accentWell.ColorPicked -= accentPicked));
        var accentHex = Bind.TextField(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, (s, value) => s.AccentColorHex = value, Translator.ApplicationThemeEditor_HexFormat, 110);
        accentHex.AutocapitalizationType = UITextAutocapitalizationType.AllCharacters;
        accentHex.AutocorrectionType = UITextAutocorrectionType.No;
        accentHex.ReturnKeyType = UIReturnKeyType.Done;
        accentHex.ShouldReturn = field => field.ResignFirstResponder();
        accentHex.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        WinoAccessibility.Label(accentHex, Translator.ApplicationThemeEditor_Accent);
        Bind.Bind(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, hex => accentWell.SetHex(hex));
        var accentControls = WinoLayout.HStack(WinoStyle.Space2, accentWell, accentHex);
        accentControls.Alignment = UIStackViewAlignment.Center;
        var accentCard = Card(Translator.ApplicationThemeEditor_CustomAccent, null, WinoIconGlyph.None, accentControls);
        Bind.Enabled(accentCard, vm, nameof(vm.UseSystemAccent), s => !s.UseSystemAccent);
        Bind.Bind(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, system => { if (system) accentWell.Flush(); });

        var expander = Expander(Translator.ApplicationThemeEditor_BaseAndAccent, Translator.ApplicationThemeEditor_BaseAndAccentDescription, WinoIconGlyph.Color, null);
        expander.Add(presetCard);
        expander.Add(_baseRows, 0, 0, 0, 0);
        expander.Add(systemCard);
        expander.Add(accentCard);
        return expander;
    }

    private static UIImage PresetSwatch(ThemeBasePreset preset)
    {
        var light = WinoStyle.FromHexString(preset.LightColor) ?? UIColor.White;
        var dark = WinoStyle.FromHexString(preset.DarkColor) ?? UIColor.Black;
        using var renderer = new UIGraphicsImageRenderer(new CGSize(28, 16));
        return renderer.CreateImage(context =>
        {
            var rect = new CGRect(0, 0, 28, 16);
            UIBezierPath.FromRoundedRect(rect, 3).AddClip();
            light.SetFill();
            UIGraphics.RectFill(new CGRect(rect.X, rect.Y, rect.Width / 2, rect.Height));
            dark.SetFill();
            UIGraphics.RectFill(new CGRect(rect.X + rect.Width / 2, rect.Y, rect.Width / 2, rect.Height));
        });
    }

    // ---- Wallpaper image ----

    /// <summary>Decodes the wallpaper once per change, in the background, for the previews and the contrast average.</summary>
    private async Task LoadWallpaperAsync(string? path)
    {
        var revision = ++_wallpaperRevision;
        string? localPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        UIImage? image = null;
        var average = string.Empty;
        if (localPath is not null)
        {
            (image, average) = await Task.Run(() =>
            {
                try
                {
                    var decoded = UIKitThemeImaging.Decode(File.ReadAllBytes(localPath));
                    return (decoded, AverageColorHex(decoded));
                }
                catch (Exception exception)
                {
                    Serilog.Log.Warning(exception, "Could not read the theme wallpaper for its preview.");
                    return ((UIImage?)null, string.Empty);
                }
            });
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released || revision != _wallpaperRevision) return;
            _wallpaperImage = image;
            ViewModel.WallpaperAverageColor = average;
            UpdatePreviews();
        });
    }

    /// <summary>The average colour of the image as #RRGGBB (the Windows editor's 8×8 average), or empty.</summary>
    private static string AverageColorHex(UIImage? image)
    {
        if (image?.CGImage is not { } source) return string.Empty;
        const int size = 8;
        var pixels = new byte[size * size * 4];
        using (var space = CGColorSpace.CreateSrgb())
        using (var context = new CGBitmapContext(pixels, size, size, 8, size * 4, space, CGImageAlphaInfo.NoneSkipLast))
        {
            context.InterpolationQuality = CGInterpolationQuality.Medium;
            context.DrawImage(new CGRect(0, 0, size, size), source);
        }

        long red = 0, green = 0, blue = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            red += pixels[index];
            green += pixels[index + 1];
            blue += pixels[index + 2];
        }
        const int count = size * size;
        return $"#{red / count:X2}{green / count:X2}{blue / count:X2}";
    }

    /// <summary>The wallpaper as the window will show it, with the focal point grid on top.</summary>
    private sealed class WallpaperPreviewView : UIView
    {
        private UIImage? _image;
        private ThemeWallpaperFit _fit;
        private ThemeWallpaperAlignment _alignment;

        public WallpaperPreviewView()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        }

        public void Update(UIImage? image, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment)
        {
            _image = image;
            _fit = fit;
            _alignment = alignment;
            SetNeedsDisplay();
        }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            var context = UIGraphics.GetCurrentContext();
            context.SaveState();
            UIBezierPath.FromRoundedRect(bounds, 6).AddClip();
            UIColor.SecondarySystemBackground.SetFill();
            UIGraphics.RectFill(bounds);
            if (_image is { } image && image.Size.Width > 0 && image.Size.Height > 0)
                image.Draw(ThemeImagePlacement.Place(image.Size, bounds, _fit, _alignment));
            context.RestoreState();
            WinoStyle.Separator.SetStroke();
            var border = UIBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 6);
            border.LineWidth = 1;
            border.Stroke();
        }
    }
}
