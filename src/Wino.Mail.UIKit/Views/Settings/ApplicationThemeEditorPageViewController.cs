using System.Collections.ObjectModel;
using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Windows ApplicationThemeEditorPage with UIKit controls: theme name, the palette being edited (Light/Dark) and Copy
/// to the other mode, the wallpaper (Browse or Replace; Fill/Fit and the 3×3 focal point), base surface presets, the
/// base colour and accent, the surface and calendar hour colours, then Cancel, Delete and Save. Every change previews
/// live through the theme service; leaving without saving restores the theme in use
/// (<see cref="ApplicationThemeEditorPageViewModel.ReleasePreviewAsync"/>). The back button asks the ViewModel first
/// (<see cref="IConfirmBackNavigation"/>), so unsaved work is not dropped silently.
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
    private readonly UIStackView _baseRows = WinoLayout.VStack(0);
    private readonly UIStackView _surfaceRows = WinoLayout.VStack(0);
    private readonly UIStackView _calendarRows = WinoLayout.VStack(0);
    private readonly UISegmentedControl _paletteMode = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISegmentedControl _wallpaperFit = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WallpaperPreviewView _wallpaperPreview = new();
    private readonly UIButton[] _focalButtons = new UIButton[9];
    private UIImage? _wallpaperImage;
    private long _wallpaperRevision;
    private bool _released;
    private bool _confirmingBack;

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

        // Name and palette mode (controls under the text on a phone).
        var name = Bind.TextField(vm, nameof(vm.ThemeName), s => s.ThemeName, (s, value) => s.ThemeName = value, Translator.ApplicationThemeEditor_Name, 0);
        name.ReturnKeyType = UIReturnKeyType.Done;
        name.ShouldReturn = field => field.ResignFirstResponder();
        name.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        WinoAccessibility.Label(name, Translator.ApplicationThemeEditor_Name);

        _paletteMode.InsertSegment(Translator.ApplicationThemeGallery_Light, 0, false);
        _paletteMode.InsertSegment(Translator.ApplicationThemeGallery_Dark, 1, false);
        WinoAccessibility.Label(_paletteMode, Translator.ApplicationThemeEditor_EditingPalette);
        Bind.OnActivated(_paletteMode, () => vm.SelectPaletteMode((int)_paletteMode.SelectedSegment), UIControlEvent.ValueChanged);
        Bind.Bind(vm, nameof(vm.PaletteModeIndex), s => s.PaletteModeIndex, index => _paletteMode.SelectedSegment = index);
        Bind.Bind(vm, nameof(vm.HasLightOverrides), s => s.HasLightOverrides, value => MarkCustomized(0, value));
        Bind.Bind(vm, nameof(vm.HasDarkOverrides), s => s.HasDarkOverrides, value => MarkCustomized(1, value));
        var copy = Bind.Button(string.Empty, vm.CopyPaletteToOtherModeCommand);
        Bind.Bind(vm, nameof(vm.CopyPaletteLabel), s => s.CopyPaletteLabel, label => SetButtonTitle(copy, label));
        var paletteContent = WinoLayout.VStack(WinoStyle.Space2, _paletteMode, copy);
        paletteContent.Alignment = UIStackViewAlignment.Leading;

        AddGroup(null,
            StackedCard(Translator.ApplicationThemeEditor_Name, Translator.ApplicationThemeEditor_NameDescription, WinoIconGlyph.Rename, name),
            StackedCard(Translator.ApplicationThemeEditor_EditingPalette, Translator.ApplicationThemeEditor_PaletteHint, WinoIconGlyph.DarkTheme, paletteContent));

        Add(BuildWallpaperExpander());
        Add(BuildBaseAndAccentExpander());

        var surfaces = Expander(Translator.ApplicationThemeEditor_Surfaces, Translator.ApplicationThemeEditor_SurfacesDescription, WinoIconGlyph.Grid, null);
        surfaces.Add(_surfaceRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenSurfaceCount), s => s.OverriddenSurfaceCount, count => surfaces.HeaderCard.Description = DescribeCount(Translator.ApplicationThemeEditor_SurfacesDescription, count));
        Add(surfaces);

        var calendar = Expander(Translator.ApplicationThemeEditor_CalendarHours, Translator.ApplicationThemeEditor_CalendarHoursDescription, WinoIconGlyph.Calendar, null);
        calendar.Add(_calendarRows, 0, 0, 0, 0);
        Bind.Bind(vm, nameof(vm.OverriddenCalendarCount), s => s.OverriddenCalendarCount, _ => calendar.HeaderCard.Description =
            DescribeCount(Translator.ApplicationThemeEditor_CalendarHoursDescription, vm.CalendarOptions.Count(option => IsShownOnIOS(option.Key) && option.IsOverridden)));
        Add(calendar);

        Bind.Collection(vm.BaseSurfaceOptions, () => RebuildRows(_baseRows, vm.BaseSurfaceOptions));
        Bind.Collection(vm.SurfaceOptions, () => RebuildRows(_surfaceRows, vm.SurfaceOptions));
        Bind.Collection(vm.CalendarOptions, () => RebuildRows(_calendarRows, vm.CalendarOptions));

        // Footer.
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        var delete = Bind.Button(Translator.Buttons_Delete, vm.DeleteCommand);
        if (delete.Configuration is { } deleteConfiguration)
        {
            deleteConfiguration.BaseForegroundColor = UIColor.SystemRed;
            delete.Configuration = deleteConfiguration;
        }
        Bind.Visible(delete, vm, nameof(vm.IsEditMode), s => s.IsEditMode);
        // A colour picked just before Save may still be in a well's debounce: write it first.
        var save = Bind.Button(Translator.Buttons_Save, () =>
        {
            foreach (var well in AllWells()) well.Flush();
            if (vm.SaveCommand.CanExecute(null)) vm.SaveCommand.Execute(null);
        }, primary: true);
        Bind.Bind(vm, nameof(vm.IsSaving), s => s.IsSaving, _ => save.Enabled = !vm.IsSaving && !vm.IsDeleting);
        Bind.Bind(vm, nameof(vm.IsDeleting), s => s.IsDeleting, _ => save.Enabled = !vm.IsSaving && !vm.IsDeleting);
        foreach (var button in new[] { cancel, delete, save }) button.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, delete, save);
        footer.Alignment = UIStackViewAlignment.Center;
        footer.LayoutMarginsRelativeArrangement = true;
        footer.DirectionalLayoutMargins = new NSDirectionalEdgeInsets((nfloat)WinoStyle.Space3, 0, (nfloat)WinoStyle.Space4, 0);
        Add(footer);

        // Previews follow the palette, the accent and the wallpaper placement.
        Bind.Bind(vm, nameof(vm.PreviewPalette), s => s.PreviewPalette, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.UseSystemAccent), s => s.UseSystemAccent, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.AccentColorHex), s => s.AccentColorHex, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.WallpaperFit), s => s.WallpaperFit, _ => UpdatePreviews());
        Bind.Bind(vm, nameof(vm.WallpaperAlignment), s => s.WallpaperAlignment, _ => { UpdateFocalButtons(); UpdatePreviews(); });
        Bind.Bind(vm, nameof(vm.WallpaperPreviewPath), s => s.WallpaperPreviewPath, path => _ = LoadWallpaperAsync(path));
        Bind.Bind(vm, nameof(vm.PageTitle), s => s.PageTitle, title =>
        {
            if (!string.IsNullOrEmpty(title)) Title = title;
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });

        // Back asks the ViewModel first: a custom back item and no swipe-back while this page is on top.
        NavigationItem.HidesBackButton = true;
        EventHandler backTapped = (_, _) => _ = ConfirmBackAsync();
        var back = UIImage.GetSystemImage("chevron.backward") is { } chevron
            ? new UIBarButtonItem(chevron, UIBarButtonItemStyle.Plain, backTapped)
            : new UIBarButtonItem(Translator.Buttons_Back, UIBarButtonItemStyle.Plain, backTapped);
        back.AccessibilityLabel = Translator.Buttons_Back;
        NavigationItem.LeftBarButtonItem = back;
    }

    private static string DescribeCount(string description, int count)
        => count > 0 ? $"{description} {string.Format(Translator.ApplicationThemeEditor_CustomizedCountFormat, count)}" : description;

    /// <summary>
    /// The iOS calendar grid has no hover or selected slot fill, so those two colours are not offered here; their saved
    /// values round-trip untouched (as on the Mac).
    /// </summary>
    private static bool IsShownOnIOS(CustomThemeColorKey key)
        => key is not (CustomThemeColorKey.CalendarHoverHour or CustomThemeColorKey.CalendarSelectedHour);

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await ViewModel.InitializeNavigationAsync(mode, parameter);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // The captured element theme can be Default: tell the VM what the app actually renders.
            ViewModel.ResolveSystemPaletteMode(WinoTraits.IsDark(View?.TraitCollection));
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
            UpdateFocalButtons();
        });
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (NavigationController?.InteractivePopGestureRecognizer is { } swipe) swipe.Enabled = false;
    }

    public override void ViewWillDisappear(bool animated)
    {
        base.ViewWillDisappear(animated);
        if (NavigationController?.InteractivePopGestureRecognizer is { } swipe) swipe.Enabled = true;
    }

    /// <summary>The back button: the ViewModel may prompt to discard unsaved changes, then the page pops.</summary>
    private async Task ConfirmBackAsync()
    {
        if (_confirmingBack) return;
        _confirmingBack = true;
        try
        {
            foreach (var well in AllWells()) well.Flush();
            if (await ViewModel.CanNavigateBackAsync())
                NavigationController?.PopViewController(true);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { _confirmingBack = false; }
    }

    protected override async Task DeactivateAsync()
    {
        // Write pending colours while the page is still live; ReleasePreviewAsync then drains that preview and restores.
        foreach (var well in AllWells()) well.Flush();
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

    private List<ThemeColorWell> AllWells() => _staticWells.Concat(_rows.Select(row => row.Well)).ToList();

    /// <summary>The small accent dot Windows shows next to a customized palette; a segment shows a title or an image, so a bullet.</summary>
    private void MarkCustomized(int segment, bool customized)
    {
        var title = segment == 0 ? Translator.ApplicationThemeGallery_Light : Translator.ApplicationThemeGallery_Dark;
        _paletteMode.SetTitle(customized ? $"{title} •" : title, segment);
    }

    private static void SetButtonTitle(UIButton button, string? title)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title ?? string.Empty;
        button.Configuration = configuration;
    }

    // ---- Palette option rows ----

    private void RebuildRows(UIStackView host, ObservableCollection<ThemePaletteColorOptionViewModel> options)
    {
        foreach (var row in _rows.Where(row => row.Host == host).ToList())
        {
            row.Dispose();
            _rows.Remove(row);
        }
        foreach (var view in host.ArrangedSubviews) { host.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        if (_released) return;

        foreach (var option in options.Where(option => IsShownOnIOS(option.Key)))
        {
            var row = new PaletteOptionRow(this, host, option);
            _rows.Add(row);
            if (host.ArrangedSubviews.Length > 0) host.AddArrangedSubview(new WinoSeparator { Fill = WinoSettingsStyle.CardStroke });
            host.AddArrangedSubview(row.Card);
        }
        UpdatePreviews();
    }

    private void ClearRows()
    {
        foreach (var row in _rows) row.Dispose();
        _rows.Clear();
    }

    private UIColor PreviewAccent()
        => !ViewModel.UseSystemAccent && WinoStyle.FromHexString(ViewModel.AccentColorHex) is { } accent ? accent : UIColor.SystemBlue;

    private void UpdatePreviews()
    {
        if (_released) return;
        var accent = PreviewAccent();
        foreach (var row in _rows)
            row.Preview.Update(ViewModel.PreviewPalette, ViewModel.IsDarkPalette, _wallpaperImage, ViewModel.WallpaperFit, ViewModel.WallpaperAlignment, accent);
        _wallpaperPreview.Update(_wallpaperImage, ViewModel.WallpaperFit, ViewModel.WallpaperAlignment);
    }

    /// <summary>A container that keeps touches on its background from reaching the tappable card around it.</summary>
    private sealed class TouchSinkView : UIView
    {
        public TouchSinkView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override void TouchesBegan(Foundation.NSSet touches, UIEvent? evt) { }
        public override void TouchesMoved(Foundation.NSSet touches, UIEvent? evt) { }
        public override void TouchesEnded(Foundation.NSSet touches, UIEvent? evt) { }
        public override void TouchesCancelled(Foundation.NSSet touches, UIEvent? evt) { }
    }

    /// <summary>One editable colour: swatch, label, value and a disclosure that opens the surface preview and the colour controls.</summary>
    private sealed class PaletteOptionRow : IDisposable
    {
        private readonly ApplicationThemeEditorPageViewController _owner;
        private readonly ThemePaletteColorOptionViewModel _option;
        private readonly CheckerSwatchView _swatch = new(28);
        private readonly UILabel _value = WinoStyle.Label(string.Empty, UIFont.GetMonospacedSystemFont(12, UIFontWeight.Regular), WinoStyle.TertiaryText);
        private readonly UIView _dot = new() { TranslatesAutoresizingMaskIntoConstraints = false };
        private readonly UITextField _hex;
        private readonly UILabel _contrast = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        private readonly UIView _panel;
        private readonly EventHandler _activated;

        public PaletteOptionRow(ApplicationThemeEditorPageViewController owner, UIStackView host, ThemePaletteColorOptionViewModel option)
        {
            _owner = owner;
            _option = option;
            Host = host;
            Card = new WinoSettingsCard(option.Label, option.Description) { IsNested = true, IsClickable = true, LeadingView = _swatch };
            WinoLayout.Size(_dot, 6, 6);
            _dot.Layer.CornerRadius = 3;
            _dot.BackgroundColor = WinoStyle.Accent;
            WinoAccessibility.Label(_dot, Translator.ApplicationThemeEditor_Customized);
            var trailing = WinoLayout.HStack(WinoStyle.Space2, _dot, _value);
            trailing.Alignment = UIStackViewAlignment.Center;
            Card.Content = trailing;
            Card.AccessibilityHint = option.Description;

            Preview = new ThemeSurfacePreviewView(option.Key, option.Scene, option.Label);
            Preview.HeightAnchor.ConstraintEqualTo(186).Active = true;
            var previewWidth = Preview.WidthAnchor.ConstraintEqualTo(248);
            previewWidth.Priority = 750;
            previewWidth.Active = true;
            Well = new ThemeColorWell(allowsAlpha: true, option.Label);
            Well.ColorPicked += WellPicked;
            _hex = new UITextField
            {
                Placeholder = Translator.ApplicationThemeEditor_HexFormatWithAlpha,
                Font = UIFont.GetMonospacedSystemFont(15, UIFontWeight.Regular),
                BorderStyle = UITextBorderStyle.RoundedRect,
                AutocapitalizationType = UITextAutocapitalizationType.AllCharacters,
                AutocorrectionType = UITextAutocorrectionType.No,
                SpellCheckingType = UITextSpellCheckingType.No,
                ReturnKeyType = UIReturnKeyType.Done,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoLayout.Size(_hex, 130, 44);
            WinoAccessibility.Label(_hex, option.Label);
            _hex.ShouldReturn = field => field.ResignFirstResponder();
            _hex.EditingDidEnd += HexCommitted;
            var reset = SettingsBinder.CreateButton(Translator.ApplicationThemeEditor_ResetToDefault);
            reset.PrimaryActionTriggered += Reset;
            var controls = WinoLayout.HStack(WinoStyle.Space2, Well, _hex);
            controls.Alignment = UIStackViewAlignment.Center;
            var hint = WinoStyle.Label(Translator.ApplicationThemeEditor_AlphaHint, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
            var panelContent = WinoLayout.VStack(WinoStyle.Space3, Preview, controls, reset, hint, _contrast);
            panelContent.Alignment = UIStackViewAlignment.Leading;
            // Taps on the panel's background must not collapse the row (the card is tappable).
            _panel = new TouchSinkView();
            WinoLayout.Fill(panelContent, _panel);
            Preview.WidthAnchor.ConstraintLessThanOrEqualTo(panelContent.WidthAnchor).Active = true;

            _activated = (_, _) =>
            {
                if (owner.ViewModel.ToggleOptionCommand.CanExecute(option)) owner.ViewModel.ToggleOptionCommand.Execute(option);
            };
            Card.Activated += _activated;
            option.PropertyChanged += OptionChanged;
            Refresh();
        }

        public UIStackView Host { get; }
        public WinoSettingsCard Card { get; }
        public ThemeSurfacePreviewView Preview { get; }
        public ThemeColorWell Well { get; }

        private void OptionChanged(object? sender, PropertyChangedEventArgs args)
            => _ = _owner.Dispatcher.ExecuteOnUIThread(() => { if (!_owner._released) Refresh(); });

        private void Refresh()
        {
            _swatch.Color = WinoStyle.FromHexString(_option.Value);
            _value.Text = _option.Value ?? string.Empty;
            _dot.Hidden = !_option.IsOverridden;
            Well.SetHex(_option.Value);
            if (!_hex.IsFirstResponder) _hex.Text = _option.Value ?? string.Empty;
            _contrast.Hidden = !_option.HasContrastReport;
            _contrast.Text = _option.ContrastMessage ?? string.Empty;
            _contrast.TextColor = _option.IsContrastSufficient ? WinoStyle.Success : WinoStyle.Caution;
            Card.SetActionIcon(_option.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight);
            Card.AccessibilityValue = _option.Value ?? string.Empty;
            var expanded = _option.IsExpanded;
            if (expanded && Card.BottomContent is null) Card.BottomContent = _panel;
            else if (!expanded && Card.BottomContent is not null)
            {
                Well.Flush();
                Card.BottomContent = null;
            }
        }

        private void WellPicked(object? sender, string hex)
        {
            if (!string.Equals(_option.Value, hex, StringComparison.OrdinalIgnoreCase)) _option.Value = hex;
        }

        private void HexCommitted(object? sender, EventArgs args)
        {
            var text = (_hex.Text ?? string.Empty).Trim();
            if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
            if (text.Length == 0 || !ThemeColorValidator.IsValidSurface(text))
            {
                _hex.Text = _option.Value ?? string.Empty;
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
            _hex.EditingDidEnd -= HexCommitted;
            Well.Flush();
        }
    }
}
