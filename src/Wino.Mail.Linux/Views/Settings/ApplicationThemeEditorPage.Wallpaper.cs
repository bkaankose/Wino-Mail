using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>The wallpaper section of the theme editor: Browse or the picture with its 3×3 focal point, Fill or Fit, Replace.</summary>
internal sealed partial class ApplicationThemeEditorPage
{
    private const int WallpaperWidth = 320;
    private const int WallpaperHeight = 200;

    private static readonly ThemeWallpaperAlignment[] FocalOrder =
    [
        ThemeWallpaperAlignment.TopLeft, ThemeWallpaperAlignment.Top, ThemeWallpaperAlignment.TopRight,
        ThemeWallpaperAlignment.Left, ThemeWallpaperAlignment.Center, ThemeWallpaperAlignment.Right,
        ThemeWallpaperAlignment.BottomLeft, ThemeWallpaperAlignment.Bottom, ThemeWallpaperAlignment.BottomRight
    ];

    private readonly Gtk.ToggleButton[] _focalButtons = new Gtk.ToggleButton[9];
    private readonly Gtk.Picture _wallpaperPicture = Gtk.Picture.New();
    private string? _wallpaperFile;
    private long _wallpaperRevision;
    private bool _syncingFocal;
#if DEBUG
    private Adw.ToggleGroup? _debugPaletteModes;
    private Adw.ToggleGroup? _debugFit;
#endif

    private Adw.ExpanderRow BuildWallpaper()
    {
        var vm = ViewModel;

        var emptyText = Gtk.Label.New(Translator.ApplicationThemeEditor_WallpaperNone);
        emptyText.AddCssClass("dim-label");
        emptyText.SetHexpand(true);
        emptyText.SetXalign(0);
        var emptyContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        emptyContent.Append(emptyText);
        emptyContent.Append(Settings.Button(Translator.Buttons_Browse, vm.ChooseWallpaperCommand));
        var empty = Settings.Visible(WinoSettingsRows.Custom(emptyContent), vm, nameof(vm.HasWallpaper), static s => !s.HasWallpaper);

        _wallpaperPicture.SetCanShrink(true);
        _wallpaperPicture.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _wallpaperPicture.SetSizeRequest(WallpaperWidth, WallpaperHeight);
        var focal = Gtk.Grid.New();
        focal.SetRowHomogeneous(true);
        focal.SetColumnHomogeneous(true);
        Gtk.ToggleButton? group = null;
        for (var index = 0; index < FocalOrder.Length; index++)
        {
            var alignment = FocalOrder[index];
            var button = Gtk.ToggleButton.New();
            button.AddCssClass("wino-te-focal");
            button.SetHalign(Gtk.Align.Center);
            button.SetValign(Gtk.Align.Center);
            if (group is null) group = button;
            else button.SetGroup(group);
            WinoA11y.LabelWithTooltip(button, FocalName(alignment));
            Settings.Own(GtkSignals.OnToggled(button, () =>
            {
                if (_syncingFocal || !button.GetActive()) return;
                if (vm.SelectFocalPointCommand.CanExecute(alignment)) vm.SelectFocalPointCommand.Execute(alignment);
                UpdateFocalButtons();
            }));
            _focalButtons[index] = button;
            focal.Attach(button, index % 3, index / 3, 1, 1);
        }
        Settings.Visible(focal, vm, nameof(vm.IsFocalSelectorEnabled), static s => s.IsFocalSelectorEnabled);
        var frame = Gtk.Overlay.New();
        frame.SetChild(WinoThemeTile.Clamped(_wallpaperPicture, WallpaperWidth));
        frame.AddOverlay(focal);
        frame.SetOverflow(Gtk.Overflow.Hidden);
        frame.AddCssClass("card");
        frame.SetHalign(Gtk.Align.Start);
        frame.SetValign(Gtk.Align.Start);
        WinoA11y.Label(frame, Translator.ApplicationThemeEditor_FocalPoint);

        var fill = Adw.Toggle.New();
        fill.SetLabel(Translator.ApplicationThemeEditor_Fill);
        var fit = Adw.Toggle.New();
        fit.SetLabel(Translator.ApplicationThemeEditor_Fit);
        var fits = Adw.ToggleGroup.New();
        fits.Add(fill);
        fits.Add(fit);
        WinoA11y.Label(fits, Translator.ApplicationThemeEditor_WallpaperFit);
        var syncingFit = false;
        Settings.Own(GtkSignals.OnNotify(fits, "active", () =>
        {
            if (!syncingFit && fits.GetActive() is var index and < 2 && (int)index != vm.WallpaperFitIndex) vm.SelectWallpaperFit((int)index);
        }));
        Settings.Bind(vm, nameof(vm.WallpaperFitIndex), static s => s.WallpaperFitIndex, index =>
        {
            syncingFit = true;
            try { fits.SetActive((uint)index); }
            finally { syncingFit = false; }
        });
        var fitTitle = Gtk.Label.New(Translator.ApplicationThemeEditor_WallpaperFit);
        var fitRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        fitRow.Append(fitTitle);
        fitRow.Append(fits);

        var focalDescription = WinoSettingsRows.Caption(null);
        focalDescription.SetMaxWidthChars(36);
        Settings.Bind(vm, nameof(vm.FocalPointDescription), static s => s.FocalPointDescription, text => focalDescription.SetText(text ?? string.Empty));
        var fileName = WinoSettingsRows.Caption(null);
        fileName.SetEllipsize(Pango.EllipsizeMode.Middle);
        fileName.SetWrap(false);
        fileName.SetMaxWidthChars(24);
        Settings.Bind(vm, nameof(vm.WallpaperFileName), static s => s.WallpaperFileName, text => fileName.SetText(text ?? string.Empty));
        var replaceRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        replaceRow.Append(fileName);
        replaceRow.Append(Settings.Button(Translator.ApplicationThemeEditor_WallpaperReplace, vm.ChooseWallpaperCommand));

        var details = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        details.Append(focalDescription);
        details.Append(fitRow);
        details.Append(replaceRow);
        var imageContent = Adw.WrapBox.New();
        imageContent.SetChildSpacing(16);
        imageContent.SetLineSpacing(12);
        imageContent.Append(frame);
        imageContent.Append(details);
        var image = Settings.Visible(WinoSettingsRows.Custom(imageContent), vm, nameof(vm.HasWallpaper), static s => s.HasWallpaper);
#if DEBUG
        _debugFit = fits;
#endif

        return WinoSettingsRows.Expander(Translator.ApplicationThemeEditor_Wallpaper, Translator.ApplicationThemeEditor_WallpaperDescription,
            WinoIconGlyph.Image, true, empty, image);
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
        _syncingFocal = true;
        try
        {
            for (var index = 0; index < _focalButtons.Length; index++)
                _focalButtons[index]?.SetActive(FocalOrder[index] == ViewModel.WallpaperAlignment);
        }
        finally { _syncingFocal = false; }
    }

    private void UpdateWallpaperPicture()
    {
        _wallpaperPicture.SetContentFit(ViewModel.WallpaperFit == ThemeWallpaperFit.Fit ? Gtk.ContentFit.Contain : Gtk.ContentFit.Cover);
    }

    /// <summary>Decodes the wallpaper once per change off the main thread, for the picture, the previews and the contrast average.</summary>
    private void LoadWallpaper(string? uri)
    {
        var revision = ++_wallpaperRevision;
        var path = LocalPath(uri);
        if (path is null)
        {
            _wallpaperFile = null;
            _wallpaperPicture.SetPaintable(null);
            ViewModel.WallpaperAverageColor = string.Empty;
            UpdatePreviews();
            return;
        }
        Observe(LoadWallpaperAsync(path, revision));
    }

    private async Task LoadWallpaperAsync(string path, long revision)
    {
        var (image, average) = await Task.Run(() => (ThemeImaging.DecodeToFit(path, WallpaperWidth * 2, WallpaperHeight * 2), ThemeImaging.AverageColorHex(path)));
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released || revision != _wallpaperRevision) return;
            _wallpaperFile = image is null ? null : path;
            _wallpaperPicture.SetPaintable(image is null ? null : ThemeImaging.ToTexture(image));
            ViewModel.WallpaperAverageColor = average;
            UpdatePreviews();
        });
    }

    private static string? LocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile) return parsed.LocalPath;
        return Path.IsPathRooted(uri) ? uri : null;
    }
}
