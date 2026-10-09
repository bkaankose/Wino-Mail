using System.Collections.ObjectModel;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Windows ApplicationThemeEditorPage, with standard AppKit controls: theme name, the palette being
/// edited (Light/Dark) and Copy to the other mode, the wallpaper (Browse, Replace or drop a JPEG/PNG;
/// Fill/Fit and the 3×3 focal point), base surface presets, the base colour and accent, the surface and
/// calendar hour colours, then Cancel, Delete and Save. Every change previews live through the theme
/// service; leaving without saving restores the theme in use (<see cref="ApplicationThemeEditorPageViewModel.ReleasePreviewAsync"/>).
/// </summary>
public sealed partial class ApplicationThemeEditorPageViewController(ApplicationThemeEditorPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ApplicationThemeEditorPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private static readonly ThemeWallpaperAlignment[] FocalOrder =
    [
        ThemeWallpaperAlignment.TopLeft, ThemeWallpaperAlignment.Top, ThemeWallpaperAlignment.TopRight,
        ThemeWallpaperAlignment.Left, ThemeWallpaperAlignment.Center, ThemeWallpaperAlignment.Right,
        ThemeWallpaperAlignment.BottomLeft, ThemeWallpaperAlignment.Bottom, ThemeWallpaperAlignment.BottomRight
    ];

    private readonly List<PaletteOptionRow> _rows = [];
    private readonly List<ThemeColorWell> _staticWells = [];
    private NSStackView _baseRows = null!;
    private NSStackView _surfaceRows = null!;
    private NSStackView _calendarRows = null!;
    private WinoSettingsExpander _surfaces = null!;
    private WinoSettingsExpander _calendar = null!;
    private NSSegmentedControl _paletteMode = null!;
    private NSSegmentedControl _wallpaperFit = null!;
    private WallpaperPreviewView _wallpaperPreview = null!;
    private readonly NSButton[] _focalButtons = new NSButton[9];
    private NSImage? _wallpaperImage;
    private long _wallpaperRevision;
    private bool _released;

    public string? PageTitle => ViewModel.PageTitle;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Error bar.
        var error = InfoBar(WinoInfoBarSeverity.Error, null, null);
        error.IsClosable = true;
        EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => vm.IsErrorOpen = false;
        error.Closed += closed;
        Bindings.Own(new ActionDisposable(() => error.Closed -= closed));
        Bind.Bind(vm, nameof(vm.ErrorMessage), s => s.ErrorMessage, message => error.Message = message);
        Bind.Visible(error, vm, nameof(vm.IsErrorOpen), s => s.IsErrorOpen);
        Add(error);

        // Name and palette mode.
        var name = Bind.TextField(vm, nameof(vm.ThemeName), s => s.ThemeName, (s, value) => s.ThemeName = value, Translator.ApplicationThemeEditor_Name, 240);
        WinoAccessibility.Label(name, Translator.ApplicationThemeEditor_Name);

        _paletteMode = new NSSegmentedControl
        {
            SegmentCount = 2,
            TrackingMode = NSSegmentSwitchTracking.SelectOne,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _paletteMode.SetLabel(Translator.ApplicationThemeGallery_Light, 0);
        _paletteMode.SetLabel(Translator.ApplicationThemeGallery_Dark, 1);
        WinoAccessibility.Label(_paletteMode, Translator.ApplicationThemeEditor_EditingPalette);
        Bind.OnActivated(_paletteMode, () => vm.SelectPaletteMode((int)_paletteMode.SelectedSegment));
        Bind.Bind(vm, nameof(vm.PaletteModeIndex), s => s.PaletteModeIndex, index => _paletteMode.SelectedSegment = index);
        Bind.Bind(vm, nameof(vm.HasLightOverrides), s => s.HasLightOverrides, value => MarkCustomized(0, value));
        Bind.Bind(vm, nameof(vm.HasDarkOverrides), s => s.HasDarkOverrides, value => MarkCustomized(1, value));
        var copy = Bind.Button(string.Empty, vm.CopyPaletteToOtherModeCommand);
        Bind.Bind(vm, nameof(vm.CopyPaletteLabel), s => s.CopyPaletteLabel, label => copy.Title = label);
        var paletteContent = WinoLayout.HStack(WinoStyle.Space2, _paletteMode, copy);
        paletteContent.Alignment = NSLayoutAttribute.CenterY;

        AddGroup(null,
            Card(Translator.ApplicationThemeEditor_Name, Translator.ApplicationThemeEditor_NameDescription, WinoIconGlyph.Rename, name),
            Card(Translator.ApplicationThemeEditor_EditingPalette, Translator.ApplicationThemeEditor_PaletteHint, WinoIconGlyph.DarkTheme, paletteContent));

        Add(BuildWallpaperExpander());
        Add(BuildBaseAndAccentExpander());

        _surfaceRows = WinoLayout.VStack(0);
        _surfaces = Expander(Translator.ApplicationThemeEditor_Surfaces, Translator.ApplicationThemeEditor_SurfacesDescription, WinoIconGlyph.Grid, null);
        _surfaces.Add(_surfaceRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenSurfaceCount), s => s.OverriddenSurfaceCount, count => _surfaces.HeaderCard.Description = DescribeCount(Translator.ApplicationThemeEditor_SurfacesDescription, count));
        Add(_surfaces);

        _calendarRows = WinoLayout.VStack(0);
        _calendar = Expander(Translator.ApplicationThemeEditor_CalendarHours, Translator.ApplicationThemeEditor_CalendarHoursDescription, WinoIconGlyph.Calendar, null);
        _calendar.Add(_calendarRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenCalendarCount), s => s.OverriddenCalendarCount, _ => _calendar.HeaderCard.Description =
            DescribeCount(Translator.ApplicationThemeEditor_CalendarHoursDescription, vm.CalendarOptions.Count(option => IsShownOnMac(option.Key) && option.IsOverridden)));
        Add(_calendar);

        Bind.Collection(vm.BaseSurfaceOptions, () => RebuildRows(_baseRows, vm.BaseSurfaceOptions));
        Bind.Collection(vm.SurfaceOptions, () => RebuildRows(_surfaceRows, vm.SurfaceOptions));
        Bind.Collection(vm.CalendarOptions, () => RebuildRows(_calendarRows, vm.CalendarOptions));

        // Footer.
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        cancel.KeyEquivalent = "\u001b";
        var delete = Bind.Button(Translator.Buttons_Delete, vm.DeleteCommand);
        delete.HasDestructiveAction = true;
        Bind.Visible(delete, vm, nameof(vm.IsEditMode), s => s.IsEditMode);
        // A colour picked just before Save may still be in a well's debounce: write it first.
        var save = Bind.Button(Translator.Buttons_Save, () =>
        {
            foreach (var well in AllWells()) well.Flush();
            if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null);
        }, primary: true);
        Bind.Bind(vm, nameof(vm.IsSaving), s => s.IsSaving, _ => save.Enabled = !vm.IsSaving && !vm.IsDeleting);
        Bind.Bind(vm, nameof(vm.IsDeleting), s => s.IsDeleting, _ => save.Enabled = !vm.IsSaving && !vm.IsDeleting);
        save.KeyEquivalent = "\r";
        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, delete, save);
        footer.EdgeInsets = new NSEdgeInsets((nfloat)WinoStyle.Space3, 0, (nfloat)WinoStyle.Space4, 0);
        Add(footer);

        // Previews follow the palette, the accent and the wallpaper placement.
        Bind.Bind(vm, nameof(vm.PreviewPalette), s => s.PreviewPalette, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.WallpaperFit), s => s.WallpaperFit, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.WallpaperAlignment), s => s.WallpaperAlignment, _ => { UpdateFocalButtons(); UpdatePreviews(); });
        Bind.Bind(vm, nameof(vm.WallpaperPreviewPath), s => s.WallpaperPreviewPath, path => _ = LoadWallpaperAsync(path));
        Bind.Bind(vm, nameof(vm.PageTitle), s => s.PageTitle, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
    }

    private static string DescribeCount(string description, int count)
        => count > 0 ? $"{description} {string.Format(Translator.ApplicationThemeEditor_CustomizedCountFormat, count)}" : description;

    /// <summary>
    /// The Mac calendar grid has no hover or selected slot fill, so those two colours are not offered
    /// here; their saved values round-trip untouched. The calendar's drag-selection fill is the future
    /// home of CalendarSelectedHour.
    /// </summary>
    private static bool IsShownOnMac(CustomThemeColorKey key)
        => key is not (CustomThemeColorKey.CalendarHoverHour or CustomThemeColorKey.CalendarSelectedHour);

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await ViewModel.InitializeNavigationAsync(mode, parameter!);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // The captured element theme can be Default: tell the VM what the app actually renders.
            ViewModel.ResolveSystemPaletteMode(WinoIcons.IsDark(View.EffectiveAppearance));
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
            UpdateFocalButtons();
        });
    }

    protected override async Task DeactivateAsync()
    {
        // Write pending colours while the page is still live; ReleasePreviewAsync then drains that preview and restores.
        foreach (var well in AllWells()) well.Deactivate();
        _released = true;
        ClearRows();
        // Restores the theme in use unless the theme was saved or deleted.
        try { await ViewModel.ReleasePreviewAsync(); }
        catch (Exception exception) { ReportError(exception); }
        await base.DeactivateAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            foreach (var well in AllWells()) well.Dispose();
            ClearRows();
        }
        base.Dispose(disposing);
    }

    private IEnumerable<ThemeColorWell> AllWells() => _staticWells.Concat(_rows.Select(row => row.Well)).ToList();

    private void MarkCustomized(int segment, bool customized)
    {
        // An empty image of the same size keeps the segment width steady when the dot goes away.
        _paletteMode.SetImage(customized ? CustomizedDot() : new NSImage(new CGSize(6, 6)), segment);
        _paletteMode.SetImageScaling(NSImageScaling.ProportionallyDown, segment);
        _paletteMode.SetToolTip(customized ? Translator.ApplicationThemeEditor_Customized : string.Empty, segment);
    }

    /// <summary>The small accent dot Windows shows next to a customized palette or colour.</summary>
    private static NSImage CustomizedDot()
        => NSImage.ImageWithSize(new CGSize(6, 6), false, rect =>
        {
            WinoStyle.Accent.SetFill();
            NSBezierPath.FromOvalInRect(rect).Fill();
            return true;
        });

    // ---- Palette option rows ----

    private void RebuildRows(NSStackView host, ObservableCollection<ThemePaletteColorOptionViewModel> options)
    {
        foreach (var row in _rows.Where(row => row.Host == host).ToList())
        {
            row.Dispose();
            _rows.Remove(row);
        }
        foreach (var view in host.ArrangedSubviews) { host.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        if (_released) return;

        foreach (var option in options.Where(option => IsShownOnMac(option.Key)))
        {
            var row = new PaletteOptionRow(this, host, option);
            _rows.Add(row);
            if (host.ArrangedSubviews.Length > 0)
            {
                var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
                host.AddArrangedSubview(separator);
                separator.WidthAnchor.ConstraintEqualTo(host.WidthAnchor).Active = true;
            }
            host.AddArrangedSubview(row.Card);
            row.Card.WidthAnchor.ConstraintEqualTo(host.WidthAnchor).Active = true;
        }
        UpdatePreviews();
    }

    private void ClearRows()
    {
        foreach (var row in _rows) row.Dispose();
        _rows.Clear();
    }

    private NSColor PreviewAccent()
        => !ViewModel.UseSystemAccent && WinoStyle.FromHexString(ViewModel.AccentColorHex) is { } accent ? accent : NSColor.ControlAccent;

    private void UpdatePreviews()
    {
        if (_released) return;
        var accent = PreviewAccent();
        foreach (var row in _rows)
            row.Preview?.Update(ViewModel.PreviewPalette, ViewModel.IsDarkPalette, _wallpaperImage, ViewModel.WallpaperFit, ViewModel.WallpaperAlignment, accent);
        _wallpaperPreview?.Update(_wallpaperImage, ViewModel.WallpaperFit, ViewModel.WallpaperAlignment);
    }

    /// <summary>A container that keeps mouse clicks from reaching the clickable card around it.</summary>
    private sealed class ClickSinkView : NSView
    {
        public ClickSinkView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override void MouseDown(NSEvent theEvent) { }
        public override void MouseUp(NSEvent theEvent) { }
    }

    /// <summary>One editable colour: swatch, label, value and a disclosure that opens the surface preview and the colour controls.</summary>
    private sealed class PaletteOptionRow : IDisposable
    {
        private readonly ApplicationThemeEditorPageViewController _owner;
        private readonly ThemePaletteColorOptionViewModel _option;
        private readonly CheckerSwatchView _swatch = new(28);
        private readonly NSTextField _value = WinoStyle.Label(string.Empty, NSFont.MonospacedSystemFont(11, NSFontWeight.Regular), WinoStyle.TertiaryText);
        private readonly NSImageView _dot = new() { TranslatesAutoresizingMaskIntoConstraints = false };
        private readonly NSTextField _hex;
        private readonly NSTextField _contrast = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        private readonly NSView _panel;
        private readonly EventHandler _activated;

        public PaletteOptionRow(ApplicationThemeEditorPageViewController owner, NSStackView host, ThemePaletteColorOptionViewModel option)
        {
            _owner = owner;
            _option = option;
            Host = host;
            Card = new WinoSettingsCard(option.Label, option.Description) { IsNested = true, IsClickable = true, LeadingView = _swatch };
            WinoLayout.Size(_dot, 6, 6);
            _dot.Image = CustomizedDot();
            _dot.ToolTip = Translator.ApplicationThemeEditor_Customized;
            var trailing = WinoLayout.HStack(WinoStyle.Space2, _dot, _value);
            trailing.Alignment = NSLayoutAttribute.CenterY;
            Card.Content = trailing;
            Card.AccessibilityHelp = option.Description;

            Preview = new ThemeSurfacePreviewView(option.Key, option.Scene, option.Label);
            WinoLayout.Size(Preview, 248, 186);
            Well = new ThemeColorWell(allowsAlpha: true, option.Label);
            Well.ColorPicked += WellPicked;
            _hex = new NSTextField { PlaceholderString = Translator.ApplicationThemeEditor_HexFormatWithAlpha, Font = NSFont.MonospacedSystemFont(12, NSFontWeight.Regular), TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(_hex, 110);
            WinoAccessibility.Label(_hex, option.Label);
            _hex.Activated += HexCommitted;
            _hex.EditingEnded += HexCommitted;
            var reset = SettingsBinder.CreateButton(Translator.ApplicationThemeEditor_ResetToDefault);
            reset.Activated += Reset;
            var controls = WinoLayout.HStack(WinoStyle.Space2, Well, _hex, reset);
            controls.Alignment = NSLayoutAttribute.CenterY;
            var hint = WinoStyle.Label(Translator.ApplicationThemeEditor_AlphaHint, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
            var side = WinoLayout.VStack(WinoStyle.Space3, controls, hint, _contrast);
            side.Alignment = NSLayoutAttribute.Leading;
            var panelContent = WinoLayout.HStack(WinoStyle.Space4, Preview, side);
            panelContent.Alignment = NSLayoutAttribute.Top;
            // Clicks on the panel's background must not collapse the row (the card is clickable).
            _panel = new ClickSinkView();
            WinoLayout.Fill(panelContent, _panel);

            _activated = (_, _) =>
            {
                if (owner.ViewModel.ToggleOptionCommand.CanExecute(option)) owner.ViewModel.ToggleOptionCommand.Execute(option);
            };
            Card.Activated += _activated;
            option.PropertyChanged += OptionChanged;
            Refresh();
        }

        public NSStackView Host { get; }
        public WinoSettingsCard Card { get; }
        public ThemeSurfacePreviewView? Preview { get; }
        public ThemeColorWell Well { get; }

        private void OptionChanged(object? sender, PropertyChangedEventArgs args)
            => _ = _owner.Dispatcher.ExecuteOnUIThread(() => { if (!_owner._released) Refresh(); });

        private void Refresh()
        {
            var color = WinoStyle.FromHexString(_option.Value);
            _swatch.Color = color;
            _value.StringValue = _option.Value ?? string.Empty;
            _dot.Hidden = !_option.IsOverridden;
            Well.SetHex(_option.Value);
            if (_hex.CurrentEditor is null) _hex.StringValue = _option.Value ?? string.Empty;
            _contrast.Hidden = !_option.HasContrastReport;
            _contrast.StringValue = _option.ContrastMessage ?? string.Empty;
            _contrast.TextColor = _option.IsContrastSufficient ? WinoStyle.Success : WinoStyle.Caution;
            Card.SetActionIcon(_option.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight);
            Card.AccessibilityExpanded = _option.IsExpanded;
            var expanded = _option.IsExpanded;
            if (expanded && Card.BottomContent is null) Card.BottomContent = _panel;
            else if (!expanded && Card.BottomContent is not null)
            {
                Well.Deactivate();
                Card.BottomContent = null;
            }
        }

        private void WellPicked(object? sender, string hex)
        {
            if (!string.Equals(_option.Value, hex, StringComparison.OrdinalIgnoreCase)) _option.Value = hex;
        }

        private void HexCommitted(object? sender, EventArgs args)
        {
            var text = _hex.StringValue.Trim();
            if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
            if (text.Length == 0 || !ThemeColorValidator.IsValidSurface(text))
            {
                _hex.StringValue = _option.Value ?? string.Empty;
                return;
            }
            if (!string.Equals(_option.Value, text, StringComparison.OrdinalIgnoreCase)) _option.Value = text.ToUpperInvariant();
        }

        private void Reset(object? sender, EventArgs args)
        {
            if (_owner.ViewModel.ResetColorCommand.CanExecute(_option)) _owner.ViewModel.ResetColorCommand.Execute(_option);
        }

        public void Dispose()
        {
            _option.PropertyChanged -= OptionChanged;
            Card.Activated -= _activated;
            Well.ColorPicked -= WellPicked;
            _hex.Activated -= HexCommitted;
            _hex.EditingEnded -= HexCommitted;
            Well.Deactivate();
        }
    }
}
