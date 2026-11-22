using System.Collections.ObjectModel;
using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The custom theme editor (Windows ApplicationThemeEditorPage, macOS twin): name and the palette being edited, the
/// wallpaper with Fill or Fit and the focal point, base presets, base colour and accent, the surface and calendar hour
/// colours, then Cancel, Delete and Save. Every change previews live through the theme service; leaving without saving
/// restores the theme in use (<see cref="ApplicationThemeEditorPageViewModel.ReleasePreviewAsync"/>).
/// </summary>
internal sealed partial class ApplicationThemeEditorPage(ApplicationThemeEditorPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ApplicationThemeEditorPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly ThemeEditorCss _css = new();
    private readonly List<PaletteOptionRow> _rows = [];
    private readonly List<WinoColorWell> _wells = [];
    private Adw.ExpanderRow? _baseAndAccent;
    private Adw.ExpanderRow? _surfaces;
    private Adw.ExpanderRow? _calendar;
    private Gtk.Widget[] _baseStaticRows = [];
    private bool _released;

#if DEBUG
    internal static ApplicationThemeEditorPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => ViewModel.PageTitle;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreview;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Settings.Own(_css);

        var error = Page.AddBanner();
        error.SetButtonLabel(Translator.Buttons_Close);
        Settings.Bind(vm, nameof(vm.ErrorMessage), static s => s.ErrorMessage, text => error.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.IsErrorOpen), static s => s.IsErrorOpen, error.SetRevealed);
        Settings.Own(GtkSignals.Connect<Adw.Banner>(h => error.OnButtonClicked += h, h => error.OnButtonClicked -= h, (_, _) => vm.IsErrorOpen = false));

        var name = Settings.Text(vm, Translator.ApplicationThemeEditor_Name, nameof(vm.ThemeName), static s => s.ThemeName, static (s, v) => s.ThemeName = v);
        WinoSettingsRows.SetGlyph(name, WinoIconGlyph.Rename);
        Page.AddGroup(null, Translator.ApplicationThemeEditor_NameDescription, name, PaletteRow());

        Page.AddGroup(null, null, BuildWallpaper());
        _baseAndAccent = BuildBaseAndAccent();
        Page.AddGroup(null, null, _baseAndAccent);

        _surfaces = WinoSettingsRows.Expander(Translator.ApplicationThemeEditor_Surfaces, Translator.ApplicationThemeEditor_SurfacesDescription, WinoIconGlyph.Grid);
        Settings.Bind(vm, nameof(vm.OverriddenSurfaceCount), static s => s.OverriddenSurfaceCount,
            count => _surfaces.SetSubtitle(DescribeCount(Translator.ApplicationThemeEditor_SurfacesDescription, count)));
        _calendar = WinoSettingsRows.Expander(Translator.ApplicationThemeEditor_CalendarHours, Translator.ApplicationThemeEditor_CalendarHoursDescription, WinoIconGlyph.Calendar);
        Settings.Bind(vm, nameof(vm.OverriddenCalendarCount), static s => s.OverriddenCalendarCount, _ => UpdateCalendarSubtitle());
        Page.AddGroup(null, null, _surfaces, _calendar);

        Settings.Collection(vm.BaseSurfaceOptions, RebuildBase);
        Settings.Collection(vm.SurfaceOptions, () => RebuildSection(_surfaces, vm.SurfaceOptions));
        Settings.Collection(vm.CalendarOptions, () =>
        {
            RebuildSection(_calendar, vm.CalendarOptions);
            UpdateCalendarSubtitle();
        });

        Page.SetBottomBar(BuildCommands());

        Settings.Bind(vm, nameof(vm.PreviewPalette), static s => s.PreviewPalette, _ => UpdatePreviews());
        Settings.Bind(vm, nameof(vm.UseSystemAccent), static s => s.UseSystemAccent, _ => UpdatePreviews());
        Settings.Bind(vm, nameof(vm.AccentColorHex), static s => s.AccentColorHex, _ => UpdatePreviews());
        Settings.Bind(vm, nameof(vm.WallpaperFit), static s => s.WallpaperFit, _ => UpdatePreviews());
        Settings.Bind(vm, nameof(vm.WallpaperAlignment), static s => s.WallpaperAlignment, _ =>
        {
            UpdateFocalButtons();
            UpdatePreviews();
        });
        Settings.Bind(vm, nameof(vm.WallpaperPreviewPath), static s => s.WallpaperPreviewPath, LoadWallpaper);
        Settings.Bind(vm, nameof(vm.PageTitle), static s => s.PageTitle, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await ViewModel.InitializeNavigationAsync(mode, parameter);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            // The captured element theme can be Default: tell the ViewModel what the app actually renders.
            ViewModel.ResolveSystemPaletteMode(Adw.StyleManager.GetDefault().Dark);
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
            UpdateFocalButtons();
        });
    }

    protected override async Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        // Pending colours are written while the page is live; ReleasePreviewAsync then drains that preview and restores.
        foreach (var well in AllWells()) well.Flush();
        _released = true;
        ClearRows(null);
        foreach (var well in _wells) well.Dispose();
        _wells.Clear();
        try { await ViewModel.ReleasePreviewAsync(); }
        catch (Exception exception) { ReportError(exception); }
        await base.DeactivateAsync();
    }

    private static string DescribeCount(string description, int count)
        => count > 0 ? $"{description} {string.Format(Translator.ApplicationThemeEditor_CustomizedCountFormat, count)}" : description;

    /// <summary>The Linux calendar has no hover or selected slot fill, as on macOS; those two colours round-trip untouched.</summary>
    private static bool IsShown(CustomThemeColorKey key)
        => key is not (CustomThemeColorKey.CalendarHoverHour or CustomThemeColorKey.CalendarSelectedHour);

    private void UpdateCalendarSubtitle()
        => _calendar?.SetSubtitle(DescribeCount(Translator.ApplicationThemeEditor_CalendarHoursDescription,
            ViewModel.CalendarOptions.Count(option => IsShown(option.Key) && option.IsOverridden)));

    private IEnumerable<WinoColorWell> AllWells() => _wells.Concat(_rows.Select(row => row.Well).OfType<WinoColorWell>()).ToList();

    private Adw.ActionRow PaletteRow()
    {
        var vm = ViewModel;
        var row = WinoSettingsRows.Action(Translator.ApplicationThemeEditor_EditingPalette, Translator.ApplicationThemeEditor_PaletteHint, WinoIconGlyph.DarkTheme);
        var light = Adw.Toggle.New();
        light.SetLabel(Translator.ApplicationThemeGallery_Light);
        var dark = Adw.Toggle.New();
        dark.SetLabel(Translator.ApplicationThemeGallery_Dark);
        var modes = Adw.ToggleGroup.New();
        modes.Add(light);
        modes.Add(dark);
        modes.SetValign(Gtk.Align.Center);
        WinoA11y.Label(modes, Translator.ApplicationThemeEditor_EditingPalette);
        var syncing = false;
        Settings.Own(GtkSignals.OnNotify(modes, "active", () =>
        {
            if (!syncing && modes.GetActive() is var index and < 2 && (int)index != vm.PaletteModeIndex) vm.SelectPaletteMode((int)index);
        }));
        Settings.Bind(vm, nameof(vm.PaletteModeIndex), static s => s.PaletteModeIndex, index =>
        {
            syncing = true;
            try { modes.SetActive((uint)index); }
            finally { syncing = false; }
        });
        void Mark(Adw.Toggle toggle, string label, bool customized)
        {
            toggle.SetLabel(customized ? label + " •" : label);
            toggle.SetTooltip(customized ? Translator.ApplicationThemeEditor_Customized : string.Empty);
        }
        Settings.Bind(vm, nameof(vm.HasLightOverrides), static s => s.HasLightOverrides, value => Mark(light, Translator.ApplicationThemeGallery_Light, value));
        Settings.Bind(vm, nameof(vm.HasDarkOverrides), static s => s.HasDarkOverrides, value => Mark(dark, Translator.ApplicationThemeGallery_Dark, value));

        var copy = Settings.Button(vm.CopyPaletteLabel, vm.CopyPaletteToOtherModeCommand);
        Settings.Bind(vm, nameof(vm.CopyPaletteLabel), static s => s.CopyPaletteLabel, label =>
        {
            copy.SetLabel(label ?? string.Empty);
            WinoA11y.Rename(copy, label ?? string.Empty);
        });

        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        box.SetValign(Gtk.Align.Center);
        box.Append(modes);
        box.Append(copy);
        WinoSettingsRows.Suffix(row, box);
#if DEBUG
        _debugPaletteModes = modes;
#endif
        return row;
    }

    private Gtk.ActionBar BuildCommands()
    {
        var vm = ViewModel;
        var bar = Gtk.ActionBar.New();
        bar.PackStart(Settings.Button(Translator.Buttons_Cancel, vm.CancelCommand));
        var delete = Settings.Visible(Settings.Button(Translator.Buttons_Delete, vm.DeleteCommand, destructive: true), vm, nameof(vm.IsEditMode), static s => s.IsEditMode);
        bar.PackEnd(Settings.Button(Translator.Buttons_Save, () =>
        {
            // A colour picked just before Save may still be in a well's debounce: write it first.
            foreach (var well in AllWells()) well.Flush();
            if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null);
        }, suggested: true));
        bar.PackEnd(delete);
        return bar;
    }

    private Adw.ExpanderRow BuildBaseAndAccent()
    {
        var vm = ViewModel;
        var presets = Adw.WrapBox.New();
        presets.SetChildSpacing(8);
        presets.SetLineSpacing(8);
        foreach (var preset in vm.BasePresets)
        {
            var swatch = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            swatch.SetOverflow(Gtk.Overflow.Hidden);
            swatch.AddCssClass("wino-te-swatch");
            foreach (var color in (string[])[preset.LightColor, preset.DarkColor])
            {
                var half = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
                half.SetSizeRequest(14, 16);
                var slot = _css.NewSlot();
                half.AddCssClass(slot);
                _css.Set(slot, color);
                swatch.Append(half);
            }
            var button = Gtk.Button.New();
            button.SetChild(swatch);
            WinoA11y.LabelWithTooltip(button, preset.Name);
            Settings.Own(GtkSignals.OnClicked(button, () => vm.ApplyBasePresetCommand.Execute(preset)));
            presets.Append(button);
        }
        var presetsTitle = Gtk.Label.New(Translator.ApplicationThemeEditor_Presets);
        presetsTitle.SetXalign(0);
        var presetsContent = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        presetsContent.Append(presetsTitle);
        presetsContent.Append(presets);
        var presetRow = WinoSettingsRows.Custom(presetsContent, Translator.ApplicationThemeEditor_Presets);

        var useSystem = Settings.Switch(vm, Translator.ApplicationThemeEditor_UseSystemAccentColor, null, WinoIconGlyph.None,
            nameof(vm.UseSystemAccent), static s => s.UseSystemAccent, static (s, v) => s.UseSystemAccent = v);

        var accent = WinoSettingsRows.Action(Translator.ApplicationThemeEditor_CustomAccent);
        var well = new WinoColorWell(allowsAlpha: false, Translator.ApplicationThemeEditor_Accent);
        _wells.Add(well);
        well.ColorPicked += hex =>
        {
            if (!_released && !string.Equals(vm.AccentColorHex, hex, StringComparison.OrdinalIgnoreCase)) vm.AccentColorHex = hex;
        };
        Settings.Bind(vm, nameof(vm.AccentColorHex), static s => s.AccentColorHex, hex => well.SetHex(hex));
        WinoSettingsRows.Suffix(accent, well.Root);
        var hexEntry = WinoSettingsRows.Suffix(accent, Settings.TextSuffix(vm, Translator.ApplicationThemeEditor_Accent, Translator.ApplicationThemeEditor_HexFormat,
            nameof(vm.AccentColorHex), static s => s.AccentColorHex, static (s, v) => s.AccentColorHex = v));
        hexEntry.SetWidthChars(9);
        Settings.Enabled(accent, vm, nameof(vm.UseSystemAccent), static s => !s.UseSystemAccent);

        _baseStaticRows = [presetRow, useSystem, accent];
        return WinoSettingsRows.Expander(Translator.ApplicationThemeEditor_BaseAndAccent, Translator.ApplicationThemeEditor_BaseAndAccentDescription,
            WinoIconGlyph.Color, false, presetRow, useSystem, accent);
    }

    /// <summary>Base colour rows sit between the presets and the accent; an expander row cannot insert, so its rows are re-added in order.</summary>
    private void RebuildBase()
    {
        if (_baseAndAccent is not { } expander || _released) return;
        ClearRows(expander);
        foreach (var row in _baseStaticRows) expander.Remove(row);
        expander.AddRow(_baseStaticRows[0]);
        foreach (var option in ViewModel.BaseSurfaceOptions.ToList()) AddOptionRow(expander, option);
        for (var index = 1; index < _baseStaticRows.Length; index++) expander.AddRow(_baseStaticRows[index]);
        UpdatePreviews();
    }

    private void RebuildSection(Adw.ExpanderRow? expander, ObservableCollection<ThemePaletteColorOptionViewModel> options)
    {
        if (expander is null || _released) return;
        ClearRows(expander);
        foreach (var option in options.Where(option => IsShown(option.Key)).ToList()) AddOptionRow(expander, option);
        UpdatePreviews();
    }

    private void AddOptionRow(Adw.ExpanderRow host, ThemePaletteColorOptionViewModel option)
    {
        var row = new PaletteOptionRow(this, host, option);
        _rows.Add(row);
        host.AddRow(row.Row);
    }

    private void ClearRows(Adw.ExpanderRow? host)
    {
        foreach (var row in _rows.Where(row => host is null || ReferenceEquals(row.Host, host)).ToList())
        {
            row.Dispose();
            if (!_released || host is not null) row.Host.Remove(row.Row);
            _rows.Remove(row);
        }
    }

    private WinoThemePreviewModel PreviewModel()
    {
        var vm = ViewModel;
        var palette = vm.PreviewPalette;
        string? accent = null;
        if (!vm.UseSystemAccent && ThemeColorValidator.TryNormalizeOpaque(vm.AccentColorHex, out var normalized)) accent = normalized;
        return new WinoThemePreviewModel(palette.MainCustomThemeColor, palette.NavigationViewContentBackground, palette.WinoContentZoneBackgroud,
            palette.MailListHeaderBackgroundColor, palette.ReadingPaneBackgroundColorBrush, palette.CalendarDefaultHourBackgroundBrush,
            palette.CalendarWorkHourBackgroundBrush, accent, _wallpaperFile, vm.WallpaperFit, vm.WallpaperAlignment);
    }

    private void UpdatePreviews()
    {
        if (_released) return;
        var model = PreviewModel();
        foreach (var row in _rows) row.Preview?.Update(model);
        UpdateWallpaperPicture();
    }

    /// <summary>One editable colour: swatch, label and value, expanding into the surface preview and the colour controls.</summary>
    private sealed class PaletteOptionRow : IDisposable
    {
        private readonly ApplicationThemeEditorPage _owner;
        private readonly ThemePaletteColorOptionViewModel _option;
        private readonly BindingScope _scope = new();
        private readonly string _swatchSlot;
        private readonly Gtk.Label _value = Gtk.Label.New(string.Empty);
        private readonly Gtk.Label _dot = Gtk.Label.New("•");
        private Adw.PreferencesRow? _panel;
        private BindingScope? _panelScope;
        private Gtk.Entry? _hex;
        private Gtk.Label? _contrast;
        private bool _syncing;

        public PaletteOptionRow(ApplicationThemeEditorPage owner, Adw.ExpanderRow host, ThemePaletteColorOptionViewModel option)
        {
            _owner = owner;
            _option = option;
            Host = host;
            Row = WinoSettingsRows.Expander(option.Label, option.Description);
            var swatch = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            swatch.SetSizeRequest(28, 28);
            swatch.SetValign(Gtk.Align.Center);
            swatch.AddCssClass("wino-te-swatch");
            _swatchSlot = owner._css.NewSlot();
            swatch.AddCssClass(_swatchSlot);
            Row.AddPrefix(swatch);
            _dot.AddCssClass("accent");
            _dot.SetTooltipText(Translator.ApplicationThemeEditor_Customized);
            _value.AddCssClass("monospace");
            _value.AddCssClass("dim-label");
            WinoSettingsRows.Suffix(Row, _dot);
            WinoSettingsRows.Suffix(Row, _value);
            _scope.Own(GtkSignals.OnNotify(Row, "expanded", () =>
            {
                if (_syncing || Row.GetExpanded() == _option.IsExpanded) return;
                if (_owner.ViewModel.ToggleOptionCommand.CanExecute(_option)) _owner.ViewModel.ToggleOptionCommand.Execute(_option);
                if (Row.GetExpanded() != _option.IsExpanded) SetExpanded(_option.IsExpanded);
            }));
            option.PropertyChanged += OptionChanged;
            Refresh();
        }

        public Adw.ExpanderRow Host { get; }
        public Adw.ExpanderRow Row { get; }
        public WinoThemePreview? Preview { get; private set; }
        public WinoColorWell? Well { get; private set; }
        public CustomThemeColorKey Key => _option.Key;

        public void Dispose()
        {
            _option.PropertyChanged -= OptionChanged;
            ClosePanel();
            _scope.Dispose();
        }

        private void OptionChanged(object? sender, PropertyChangedEventArgs args)
            => _ = _owner.Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!_owner._released && !_scope.IsDisposed) Refresh();
            });

        private void Refresh()
        {
            _owner._css.Set(_swatchSlot, _option.Value);
            _value.SetText(_option.Value ?? string.Empty);
            _dot.SetVisible(_option.IsOverridden);
            if (_option.IsExpanded && _panel is null) OpenPanel();
            else if (!_option.IsExpanded && _panel is not null) ClosePanel();
            SetExpanded(_option.IsExpanded);
            Well?.SetHex(_option.Value);
            if (_hex is { } hex && !hex.HasFocus && hex.GetFocusChild() is null) hex.SetText(_option.Value ?? string.Empty);
            if (_contrast is { } contrast)
            {
                contrast.SetVisible(_option.HasContrastReport);
                contrast.SetText(_option.ContrastMessage ?? string.Empty);
                contrast.RemoveCssClass(_option.IsContrastSufficient ? "warning" : "success");
                contrast.AddCssClass(_option.IsContrastSufficient ? "success" : "warning");
            }
        }

        private void SetExpanded(bool expanded)
        {
            if (Row.GetExpanded() == expanded) return;
            _syncing = true;
            try { Row.SetExpanded(expanded); }
            finally { _syncing = false; }
        }

        private void OpenPanel()
        {
            var scope = _panelScope = new BindingScope();
            Preview = new WinoThemePreview(_option.Key, _option.Scene, _option.Label);
            var well = Well = new WinoColorWell(allowsAlpha: true, _option.Label);
            well.SetHex(_option.Value);
            well.ColorPicked += hex =>
            {
                if (!string.Equals(_option.Value, hex, StringComparison.OrdinalIgnoreCase)) _option.Value = hex;
            };
            var hex = _hex = Gtk.Entry.New();
            hex.SetPlaceholderText(Translator.ApplicationThemeEditor_HexFormatWithAlpha);
            hex.SetWidthChars(11);
            hex.SetValign(Gtk.Align.Center);
            hex.AddCssClass("monospace");
            hex.SetText(_option.Value ?? string.Empty);
            WinoA11y.Label(hex, _option.Label);
            scope.Own(GtkSignals.Connect<Gtk.Entry>(h => hex.OnActivate += h, h => hex.OnActivate -= h, (_, _) => CommitHex()));
            var focus = Gtk.EventControllerFocus.New();
            hex.AddController(focus);
            scope.Own(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnLeave += h, h => focus.OnLeave -= h, (_, _) => CommitHex()));
            var reset = Gtk.Button.NewWithLabel(Translator.ApplicationThemeEditor_ResetToDefault);
            reset.SetValign(Gtk.Align.Center);
            scope.Own(GtkSignals.OnClicked(reset, () =>
            {
                if (_owner.ViewModel.ResetColorCommand.CanExecute(_option)) _owner.ViewModel.ResetColorCommand.Execute(_option);
            }));

            var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            controls.Append(well.Root);
            controls.Append(hex);
            controls.Append(reset);
            var hint = WinoSettingsRows.Caption(Translator.ApplicationThemeEditor_AlphaHint);
            _contrast = WinoSettingsRows.Caption(null);
            _contrast.RemoveCssClass("dim-label");
            var side = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
            side.SetHexpand(true);
            side.Append(controls);
            side.Append(hint);
            side.Append(_contrast);
            var content = Adw.WrapBox.New();
            content.SetChildSpacing(16);
            content.SetLineSpacing(12);
            content.Append(Preview.Root);
            content.Append(side);
            _panel = WinoSettingsRows.Custom(content);
            Row.AddRow(_panel);
            Preview.Update(_owner.PreviewModel());
        }

        private void ClosePanel()
        {
            if (_panel is { } panel) Row.Remove(panel);
            _panel = null;
            Well?.Flush();
            Well?.Dispose();
            Well = null;
            Preview?.Dispose();
            Preview = null;
            _panelScope?.Dispose();
            _panelScope = null;
            _hex = null;
            _contrast = null;
        }

        private void CommitHex()
        {
            if (_hex is not { } entry) return;
            var text = entry.GetText().Trim();
            if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
            if (text.Length == 0 || !ThemeColorValidator.IsValidSurface(text))
            {
                entry.SetText(_option.Value ?? string.Empty);
                return;
            }
            if (!string.Equals(_option.Value, text, StringComparison.OrdinalIgnoreCase)) _option.Value = text.ToUpperInvariant();
        }
    }
}
