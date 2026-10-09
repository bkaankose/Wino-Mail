using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>The wallpaper and the base surface and accent sections of the theme editor.</summary>
public sealed partial class ApplicationThemeEditorPageViewController
{
    private WinoSettingsExpander BuildWallpaperExpander()
    {
        var vm = ViewModel;

        // No image yet: a drop zone with Browse.
        var browse = Bind.Button(Translator.Buttons_Browse, vm.ChooseWallpaperCommand);
        var emptyText = WinoStyle.Label(Translator.ApplicationThemeEditor_WallpaperNone, WinoStyle.Body, WinoStyle.SecondaryText);
        var emptyRow = WinoLayout.HStack(WinoStyle.Space3, emptyText, WinoLayout.Spacer(), browse);
        emptyRow.Alignment = NSLayoutAttribute.CenterY;
        var empty = new WallpaperDropView(DropWallpaper, dashed: true);
        WinoLayout.Fill(emptyRow, empty, 14, 14, 14, 14);
        Bind.Visible(empty, vm, nameof(vm.HasWallpaper), s => !s.HasWallpaper);

        // An image: the preview with the focal point grid, Fill/Fit, the file name and Replace.
        _wallpaperPreview = new WallpaperPreviewView();
        WinoLayout.Size(_wallpaperPreview, 320, 200);
        WinoAccessibility.Label(_wallpaperPreview, Translator.ApplicationThemeEditor_FocalPoint);
        var focalGrid = new NSGridView { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 0, ColumnSpacing = 0 };
        for (int row = 0; row < 3; row++)
        {
            var cells = new NSView[3];
            for (int column = 0; column < 3; column++)
            {
                int index = row * 3 + column;
                var alignment = FocalOrder[index];
                var button = new NSButton
                {
                    Title = string.Empty,
                    TranslatesAutoresizingMaskIntoConstraints = false
                };
                button.SetButtonType(NSButtonType.Radio);
                WinoAccessibility.Label(button, FocalName(alignment));
                button.ToolTip = FocalName(alignment);
                Bind.OnActivated(button, () =>
                {
                    if (vm.SelectFocalPointCommand.CanExecute(alignment)) vm.SelectFocalPointCommand.Execute(alignment);
                    UpdateFocalButtons();
                });
                _focalButtons[index] = button;
                var cell = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
                WinoLayout.Size(cell, 320 / 3.0, 200 / 3.0);
                cell.AddSubview(button);
                NSLayoutConstraint.ActivateConstraints([button.CenterXAnchor.ConstraintEqualTo(cell.CenterXAnchor), button.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor)]);
                cells[column] = cell;
            }
            focalGrid.AddRow(cells);
        }
        _wallpaperPreview.AddSubview(focalGrid);
        WinoLayout.Fill(focalGrid, _wallpaperPreview);
        Bind.Bind(vm, nameof(vm.IsFocalSelectorEnabled), s => s.IsFocalSelectorEnabled, enabled =>
        {
            foreach (var button in _focalButtons) button.Hidden = !enabled;
        });

        _wallpaperFit = new NSSegmentedControl { SegmentCount = 2, TrackingMode = NSSegmentSwitchTracking.SelectOne, TranslatesAutoresizingMaskIntoConstraints = false };
        _wallpaperFit.SetLabel(Translator.ApplicationThemeEditor_Fill, 0);
        _wallpaperFit.SetLabel(Translator.ApplicationThemeEditor_Fit, 1);
        WinoAccessibility.Label(_wallpaperFit, Translator.ApplicationThemeEditor_WallpaperFit);
        Bind.OnActivated(_wallpaperFit, () => vm.SelectWallpaperFit((int)_wallpaperFit.SelectedSegment));
        Bind.Bind(vm, nameof(vm.WallpaperFitIndex), s => s.WallpaperFitIndex, index => _wallpaperFit.SelectedSegment = index);
        var fitRow = WinoLayout.HStack(WinoStyle.Space2, WinoStyle.Label(Translator.ApplicationThemeEditor_WallpaperFit, WinoStyle.Body), _wallpaperFit);
        fitRow.Alignment = NSLayoutAttribute.CenterY;

        var focalDescription = Bind.Label(vm, nameof(vm.FocalPointDescription), s => s.FocalPointDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        focalDescription.PreferredMaxLayoutWidth = 320;
        var fileName = Bind.Label(vm, nameof(vm.WallpaperFileName), s => s.WallpaperFileName, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var replace = Bind.Button(Translator.ApplicationThemeEditor_WallpaperReplace, vm.ChooseWallpaperCommand);
        var replaceRow = WinoLayout.HStack(WinoStyle.Space2, fileName, replace);
        replaceRow.Alignment = NSLayoutAttribute.CenterY;
        var details = WinoLayout.VStack(WinoStyle.Space3, focalDescription, fitRow, replaceRow);
        details.Alignment = NSLayoutAttribute.Leading;
        var imageRow = WinoLayout.HStack(WinoStyle.Space4, _wallpaperPreview, details);
        imageRow.Alignment = NSLayoutAttribute.Top;
        var image = new WallpaperDropView(DropWallpaper, dashed: false);
        WinoLayout.Fill(imageRow, image, 12, 0, 12, 0);
        Bind.Visible(image, vm, nameof(vm.HasWallpaper), s => s.HasWallpaper);

        var content = WinoLayout.VStack(0, empty, image);
        content.Alignment = NSLayoutAttribute.Leading;
        empty.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
        var expander = Expander(Translator.ApplicationThemeEditor_Wallpaper, Translator.ApplicationThemeEditor_WallpaperDescription, WinoIconGlyph.Image, null);
        expander.Add(content, 10, WinoSettingsStyle.NestedIndent, 10, WinoSettingsStyle.CardPadding);
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
        for (int index = 0; index < _focalButtons.Length; index++)
            if (_focalButtons[index] is { } button)
                button.State = FocalOrder[index] == ViewModel.WallpaperAlignment ? NSCellStateValue.On : NSCellStateValue.Off;
    }

    private WinoSettingsExpander BuildBaseAndAccentExpander()
    {
        var vm = ViewModel;

        // Presets: one bordered button per preset, its swatch half light and half dark.
        var presets = WinoLayout.HStack(WinoStyle.Space2);
        foreach (var preset in vm.BasePresets)
        {
            var button = new NSButton
            {
                Title = string.Empty,
                BezelStyle = NSBezelStyle.Rounded,
                Image = PresetSwatch(preset),
                ImagePosition = NSCellImagePosition.ImageOnly,
                ToolTip = preset.Name,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoAccessibility.Label(button, preset.Name);
            Bind.OnActivated(button, () => vm.ApplyBasePresetCommand.Execute(preset));
            presets.AddArrangedSubview(button);
        }
        var presetCard = Card(Translator.ApplicationThemeEditor_Presets, null, WinoIconGlyph.None, presets);

        _baseRows = WinoLayout.VStack(0);

        var useSystem = Bind.Switch(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, (s, value) => s.UseSystemAccent = value, Translator.ApplicationThemeEditor_UseSystemAccent);
        var systemCard = Card(Translator.ApplicationThemeEditor_UseSystemAccent, null, WinoIconGlyph.None, useSystem);

        var accentWell = new ThemeColorWell(allowsAlpha: false, Translator.ApplicationThemeEditor_Accent);
        _staticWells.Add(accentWell);
        EventHandler<string> accentPicked = (_, hex) =>
        {
            if (!string.Equals(vm.AccentColorHex, hex, StringComparison.OrdinalIgnoreCase)) vm.AccentColorHex = hex;
        };
        accentWell.ColorPicked += accentPicked;
        Bindings.Own(new ActionDisposable(() => accentWell.ColorPicked -= accentPicked));
        var accentHex = Bind.TextField(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, (s, value) => s.AccentColorHex = value, Translator.ApplicationThemeEditor_HexFormat, 100);
        WinoAccessibility.Label(accentHex, Translator.ApplicationThemeEditor_Accent);
        Bind.Bind(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, hex => accentWell.SetHex(hex));
        var accentControls = WinoLayout.HStack(WinoStyle.Space2, accentWell, accentHex);
        accentControls.Alignment = NSLayoutAttribute.CenterY;
        var accentCard = Card(Translator.ApplicationThemeEditor_CustomAccent, null, WinoIconGlyph.None, accentControls);
        Bind.Enabled(accentCard, vm, nameof(vm.UseSystemAccent), s => !s.UseSystemAccent);
        Bind.Bind(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, system => { if (system) accentWell.Deactivate(); });

        var expander = Expander(Translator.ApplicationThemeEditor_BaseAndAccent, Translator.ApplicationThemeEditor_BaseAndAccentDescription, WinoIconGlyph.Color, null);
        expander.Add(presetCard);
        expander.Add(_baseRows, 0, 0, 0, 0);
        expander.Add(systemCard);
        expander.Add(accentCard);
        return expander;
    }

    private static NSImage PresetSwatch(ThemeBasePreset preset)
    {
        var light = WinoStyle.FromHexString(preset.LightColor) ?? NSColor.White;
        var dark = WinoStyle.FromHexString(preset.DarkColor) ?? NSColor.Black;
        return NSImage.ImageWithSize(new CGSize(28, 16), false, rect =>
        {
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            NSBezierPath.FromRoundedRect(rect, 3, 3).AddClip();
            light.SetFill();
            NSGraphics.RectFill(new CGRect(rect.X, rect.Y, rect.Width / 2, rect.Height));
            dark.SetFill();
            NSGraphics.RectFill(new CGRect(rect.X + rect.Width / 2, rect.Y, rect.Width / 2, rect.Height));
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            return true;
        });
    }

    // ---- Wallpaper image ----

    /// <summary>A dropped Finder file: read it, check that it is a JPEG or PNG image, and hand it to the VM.</summary>
    private void DropWallpaper(string path)
    {
        _ = Task.Run(async () =>
        {
            byte[]? bytes = null;
            try { bytes = await File.ReadAllBytesAsync(path); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Could not read the dropped wallpaper."); }
            var valid = MacThemeImaging.IsImage(bytes);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (_released) return;
                if (!valid || bytes is null)
                {
                    ViewModel.ErrorMessage = Translator.MacPlatform_ThemeWallpaperInvalid;
                    ViewModel.IsErrorOpen = true;
                    return;
                }
                ViewModel.ApplyWallpaper(bytes, path, Path.GetFileName(path));
            });
        });
    }

    /// <summary>Decodes the wallpaper once per change, in the background, for the previews and the contrast average.</summary>
    private async Task LoadWallpaperAsync(string? path)
    {
        var revision = ++_wallpaperRevision;
        string? localPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        NSImage? image = null;
        var average = string.Empty;
        if (localPath is not null)
        {
            (image, average) = await Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(localPath);
                    return (MacThemeImaging.Decode(bytes), MacThemeImaging.AverageColorHex(bytes));
                }
                catch (Exception exception)
                {
                    Serilog.Log.Warning(exception, "Could not read the theme wallpaper for its preview.");
                    return ((NSImage?)null, string.Empty);
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

    /// <summary>The wallpaper as the window will show it, with the focal point grid on top.</summary>
    private sealed class WallpaperPreviewView : NSView
    {
        private NSImage? _image;
        private ThemeWallpaperFit _fit;
        private ThemeWallpaperAlignment _alignment;

        public WallpaperPreviewView() => TranslatesAutoresizingMaskIntoConstraints = false;

        public override bool IsFlipped => true;

        public void Update(NSImage? image, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment)
        {
            _image = image;
            _fit = fit;
            _alignment = alignment;
            NeedsDisplay = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            NSBezierPath.FromRoundedRect(bounds, 6, 6).AddClip();
            NSColor.WindowBackground.SetFill();
            NSGraphics.RectFill(bounds);
            if (_image is { } image && image.Size.Width > 0 && image.Size.Height > 0)
                image.Draw(MacThemeImaging.Place(image.Size, bounds, _fit, _alignment, flipped: true), CGRect.Empty, NSCompositingOperation.SourceOver, 1, true, null);
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            WinoStyle.Separator.SetStroke();
            var border = NSBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 6, 6);
            border.LineWidth = 1;
            border.Stroke();
        }
    }

    /// <summary>Accepts a JPEG or PNG file dragged from Finder.</summary>
    private sealed class WallpaperDropView : NSView
    {
        private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];
        private readonly Action<string> _dropped;
        private readonly bool _dashed;
        private bool _targeted;

        public WallpaperDropView(Action<string> dropped, bool dashed)
        {
            _dropped = dropped;
            _dashed = dashed;
            TranslatesAutoresizingMaskIntoConstraints = false;
            RegisterForDraggedTypes(["public.file-url"]);
        }

        private static string? FilePath(INSDraggingInfo info)
        {
            var value = info.DraggingPasteboard.GetStringForType("public.file-url");
            var path = value is null ? null : NSUrl.FromString(value)?.FilePathUrl?.Path;
            return path is not null && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? path : null;
        }

        public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
        {
            _targeted = FilePath(sender) is not null;
            NeedsDisplay = true;
            return _targeted ? NSDragOperation.Copy : NSDragOperation.None;
        }

        public override void DraggingExited(INSDraggingInfo? sender)
        {
            _targeted = false;
            NeedsDisplay = true;
        }

        public override bool PerformDragOperation(INSDraggingInfo sender)
        {
            _targeted = false;
            NeedsDisplay = true;
            if (FilePath(sender) is not { } path) return false;
            _dropped(path);
            return true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            if (!_dashed && !_targeted) return;
            var path = NSBezierPath.FromRoundedRect(Bounds.Inset(1, 1), 6, 6);
            path.LineWidth = _targeted ? 2 : 1;
            if (!_targeted) path.SetLineDash([4, 3], 0);
            (_targeted ? WinoStyle.Accent : WinoStyle.Separator).SetStroke();
            path.Stroke();
        }
    }
}
