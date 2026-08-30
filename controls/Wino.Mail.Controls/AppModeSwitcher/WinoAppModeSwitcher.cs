using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Wino.Mail.Controls.AppModeSwitcher;

/// <summary>
/// A strip of mutually exclusive destinations: the app's modes, with settings as the last
/// one. Every cell shares a single sliding selection tile, so exactly one is ever lit.
///
/// Settings is still distinct in what it means - it raises <see cref="SettingsInvoked"/>
/// rather than <see cref="ModeInvoked"/>, and the host tracks it separately - but it is not
/// distinct in how it behaves. It is the one cell drawn from an <see cref="AnimatedIcon"/>
/// instead of app artwork, so it has no resting chip: a line glyph has no white regions to
/// rescue from the card.
///
/// The artwork is recoloured rather than shipped per theme: <see cref="AppModeGlyphPalette"/>
/// substitutes the app accent into the glyph blues, so one asset serves both themes. Light
/// rests each glyph on a chip tinted from that same accent, which becomes the selection tile
/// when the mode is picked. Dark needs no chip, because its card is already darker than the
/// artwork; its tile lifts away from the card instead of inverting against it, which is what
/// keeps the white regions of the artwork readable in every state.
///
/// The control carries no visual states. Every appearance change is applied from code and
/// every transition is a Composition animation, so there is no storyboard and no
/// <see cref="VisualStateManager"/> in the control or its template. The selection tile
/// slides on "Translation" rather than "Offset" because XAML owns an element's own offset
/// and would overwrite it on the next layout pass.
///
/// The control is domain agnostic: it never names mail, calendars, contacts or tasks. The
/// host fills <see cref="Items"/>, handles the events, and decides whether the selection
/// actually moves.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public sealed partial class WinoAppModeSwitcher : Control
{
    private const string ModeHostPartName = "PART_ModeHost";
    private const string SelectionIndicatorPartName = "PART_SelectionIndicator";
    private const string SettingsButtonPartName = "PART_SettingsButton";
    private const string SettingsIconPartName = "PART_SettingsIcon";

    private const string NormalIconState = "Normal";
    private const string PointerOverIconState = "PointerOver";
    private const string PressedIconState = "Pressed";

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PointerScaleDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SelectionPopDuration = TimeSpan.FromMilliseconds(220);

    private const float HoverScale = 1.06f;
    private const float PressedScale = 0.94f;
    private const float SelectionPopScale = 1.12f;

    /// <summary>
    /// The artwork size. Only the glyph is this big; the cell around it is larger.
    /// </summary>
    private const double GlyphExtent = 30d;

    /// <summary>
    /// The cell's minor axis - its height when horizontal, its width when vertical. Larger
    /// than the glyph so the artwork has room off the tile's edges instead of touching them.
    /// </summary>
    private const double CellExtent = 38d;

    /// <summary>
    /// Cells are star sized, so they meet edge to edge and their chips would fuse into one
    /// bar without this. It also keeps the stacked rail icons scanning as distinct targets.
    /// </summary>
    private const double CellSpacing = 4d;

    /// <summary>
    /// Cells are wider than they are tall in an open pane, so the radius is the tile's own
    /// rather than the app tiles' 24% of their width.
    /// </summary>
    private static readonly CornerRadius CellCornerRadius = new(8d);

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly List<Border> _containers = [];
    private readonly List<Border> _wells = [];

    private Grid? _modeHost;
    private Border? _selectionIndicator;
    private Grid? _settingsButton;
    private Border? _settingsChip;
    private AnimatedIcon? _settingsIcon;
    private bool _isTemplateApplied;

    /// <summary>
    /// Glyphs are built asynchronously, so a rebuild that starts while an earlier one is
    /// still running has to be able to discard it rather than let it land late.
    /// </summary>
    private int _glyphGeneration;

    /// <summary>
    /// The modes, in the order they appear. Filled as the control's XAML content.
    /// </summary>
    public ObservableCollection<WinoAppModeSwitcherItem> Items { get; } = [];

    /// <summary>
    /// The selected mode. -1 selects nothing, which is how a host shows that some other
    /// surface currently owns the window.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = 0)]
    public partial int SelectedIndex { get; set; }

    /// <summary>
    /// Horizontal in an open pane, vertical in a collapsed rail.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = Orientation.Horizontal)]
    public partial Orientation Orientation { get; set; }

    /// <summary>
    /// Moves the selection onto the settings cell. Kept separate from
    /// <see cref="SelectedIndex"/> because settings is not one of the items, but the two
    /// resolve to a single lit cell: settings wins while it is set.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSettingsSelected { get; set; }

    /// <summary>
    /// Tooltip and automation name for the settings cell.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SettingsLabel { get; set; }

    /// <summary>
    /// Lets a host reuse the mode strip on its own.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSettingsVisible { get; set; }

    // The per-state foregrounds are properties rather than resource lookups because the
    // brushes live in this control's own theme dictionary: only the template can reach them,
    // and it hands them over here.

    /// <summary>
    /// A mode at rest. Only reaches items that supply their own
    /// <see cref="WinoAppModeSwitcherItem.Icon"/>; recoloured artwork carries its own colour.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeForeground { get; set; }

    /// <summary>
    /// A mode under the pointer.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeHoverForeground { get; set; }

    /// <summary>
    /// Whatever sits on the selection tile, which in practice is the settings glyph: the
    /// tile is a near-neutral, so this is its contrasting foreground rather than an accent.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeSelectedForeground { get; set; }

    /// <summary>
    /// The settings glyph at rest.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? SettingsForeground { get; set; }

    /// <summary>
    /// Overrides the resting chip. Left unset - which is the normal case - the chip is
    /// derived from the app accent and the theme, so it stays a fixed weight whatever accent
    /// the user has picked and disappears in Dark.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? RestingWellBrush { get; set; }

    /// <summary>
    /// Raised when an item is clicked or activated from the keyboard.
    /// </summary>
    public event EventHandler<WinoAppModeInvokedEventArgs>? ModeInvoked;

    /// <summary>
    /// Raised when the settings cell is clicked or activated from the keyboard.
    /// </summary>
    public event EventHandler? SettingsInvoked;

    public WinoAppModeSwitcher()
    {
        DefaultStyleKey = typeof(WinoAppModeSwitcher);

        Items.CollectionChanged += OnItemsChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DetachTemplateParts();

        _modeHost = GetTemplateChild(ModeHostPartName) as Grid;
        _selectionIndicator = GetTemplateChild(SelectionIndicatorPartName) as Border;
        _settingsButton = GetTemplateChild(SettingsButtonPartName) as Grid;
        _settingsIcon = GetTemplateChild(SettingsIconPartName) as AnimatedIcon;

        if (_modeHost is not null)
        {
            // The tile is sized from a cell, so a resize has to re-seat it even though no
            // selection changed.
            _modeHost.SizeChanged += OnModeHostSizeChanged;
        }

        if (XamlRoot is not null)
        {
            // Moving the window to a display with a different scale changes what "sharp"
            // means, and a stream-loaded glyph cannot re-rasterise itself.
            XamlRoot.Changed += OnXamlRootChanged;
        }

        if (_selectionIndicator is not null)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_selectionIndicator, true);
        }

        if (_settingsButton is not null && _settingsChip is null)
        {
            // Settings takes a chip like any other cell. It is the odd glyph out - a line
            // icon rather than app artwork - and without one it reads as a gap in the strip.
            _settingsChip = CreateChip();
            _modeHost?.Children.Insert(0, _settingsChip);
        }

        if (_settingsButton is not null)
        {
            _settingsButton.PointerEntered += OnSettingsPointerEntered;
            _settingsButton.PointerExited += OnSettingsPointerExited;
            _settingsButton.PointerPressed += OnSettingsPointerPressed;
            _settingsButton.PointerReleased += OnSettingsPointerReleased;
            _settingsButton.PointerCanceled += OnSettingsPointerExited;
            _settingsButton.PointerCaptureLost += OnSettingsPointerExited;
            _settingsButton.Tapped += OnSettingsTapped;
            _settingsButton.KeyDown += OnSettingsKeyDown;
            _settingsButton.SizeChanged += OnElementSizeChanged;
        }

        _isTemplateApplied = true;

        RebuildItems();
        ApplyOrientation();
        ApplySettingsLabel();

        // Arriving in a state is not a transition; the control simply shows what the host
        // already set.
        UpdateSelection(animate: false);
    }

    /// <summary>
    /// Rebuilds the artwork against the accent the app is using now. The accent lives in an
    /// application resource that is mutated in place, so nothing raises a property change
    /// the control could listen to: the host says when.
    /// </summary>
    public void RefreshAccent() => UpdateSelection(animate: false, popSelection: false);

    #region Property callbacks

    partial void OnSelectedIndexChanged(int newValue) => UpdateSelection(animate: true);

    partial void OnOrientationChanged(Orientation newValue)
    {
        ApplyOrientation();
        UpdateSelection(animate: false);
    }

    partial void OnIsSettingsSelectedChanged(bool newValue) => UpdateSelection(animate: true);

    partial void OnIsSettingsVisibleChanged(bool newValue)
    {
        ApplyOrientation();
        UpdateSelection(animate: false);
    }

    partial void OnSettingsLabelChanged(string newValue) => ApplySettingsLabel();

    // A theme change swaps the brushes underneath, so the assigned foregrounds have to be
    // re-applied rather than left on the old theme's colours.
    partial void OnModeForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnModeHoverForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnModeSelectedForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnSettingsForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnRestingWellBrushChanged(Brush? newValue) => UpdateSelection(animate: false, popSelection: false);

    private void RefreshForegrounds() => UpdateSelection(animate: false, popSelection: false);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildItems();
        ApplyOrientation();
        UpdateSelection(animate: false);
    }

    #endregion

    #region Item containers

    /// <summary>
    /// Items are realized as plain borders rather than buttons: a button brings its own
    /// visual state machine, which is exactly what this control is meant to do without.
    /// </summary>
    private void RebuildItems()
    {
        if (!_isTemplateApplied || _modeHost is null)
            return;

        ClearContainers();
        ClearWells();

        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];

            var well = CreateChip();

            // Chips sit under the selection tile so a slide still reads as the tile moving,
            // not as a fill on the hit-test container above it.
            _modeHost.Children.Insert(_wells.Count, well);
            _wells.Add(well);

            // The container fills its star sized cell so the whole cell is the hit target,
            // and the glyph sits centred inside it at its own smaller size.
            var container = new Border
            {
                CornerRadius = CellCornerRadius,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsTabStop = true,
                UseSystemFocusVisuals = true,
                Tag = index,
                Child = item.GlyphSource is null ? item.Icon : CreateGlyphHost()
            };

            AutomationProperties.SetName(container, item.Label);
            ToolTipService.SetToolTip(container, string.IsNullOrEmpty(item.Label) ? null : item.Label);

            container.PointerEntered += OnItemPointerEntered;
            container.PointerExited += OnItemPointerExited;
            container.PointerPressed += OnItemPointerPressed;
            container.PointerReleased += OnItemPointerReleased;
            container.PointerCanceled += OnItemPointerExited;
            container.PointerCaptureLost += OnItemPointerExited;
            container.Tapped += OnItemTapped;
            container.KeyDown += OnItemKeyDown;
            container.SizeChanged += OnElementSizeChanged;

            _containers.Add(container);
            _modeHost.Children.Add(container);
        }
    }

    /// <summary>
    /// The recoloured artwork arrives later than layout does, so the container gets its
    /// image host straight away and the source is filled in when it is ready.
    /// </summary>
    private static ImageIcon CreateGlyphHost()
        => new()
        {
            Width = GlyphExtent,
            Height = GlyphExtent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    /// <summary>
    /// The fill behind a resting glyph. It fills the whole cell, so selecting the cell reads
    /// as this chip deepening in place rather than as a different shape arriving.
    /// </summary>
    private static Border CreateChip()
        => new()
        {
            CornerRadius = CellCornerRadius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => UpdateSelection(animate: false, popSelection: false);

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => UpdateSelection(animate: false, popSelection: false);

    private void ClearContainers()
    {
        foreach (var container in _containers)
        {
            DetachContainer(container);
            _modeHost?.Children.Remove(container);
        }

        _containers.Clear();
    }

    private void ClearWells()
    {
        foreach (var well in _wells)
        {
            _modeHost?.Children.Remove(well);
        }

        _wells.Clear();
    }

    private void DetachContainer(Border container)
    {
        container.PointerEntered -= OnItemPointerEntered;
        container.PointerExited -= OnItemPointerExited;
        container.PointerPressed -= OnItemPointerPressed;
        container.PointerReleased -= OnItemPointerReleased;
        container.PointerCanceled -= OnItemPointerExited;
        container.PointerCaptureLost -= OnItemPointerExited;
        container.Tapped -= OnItemTapped;
        container.KeyDown -= OnItemKeyDown;
        container.SizeChanged -= OnElementSizeChanged;

        // A host-supplied icon belongs to the item, not the container, so it has to be
        // released before the container goes away or the next rebuild cannot reparent it.
        container.Child = null;
    }

    private void DetachTemplateParts()
    {
        ClearContainers();
        ClearWells();

        if (_modeHost is not null)
        {
            _modeHost.SizeChanged -= OnModeHostSizeChanged;
        }

        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }

        if (_settingsButton is not null)
        {
            _settingsButton.PointerEntered -= OnSettingsPointerEntered;
            _settingsButton.PointerExited -= OnSettingsPointerExited;
            _settingsButton.PointerPressed -= OnSettingsPointerPressed;
            _settingsButton.PointerReleased -= OnSettingsPointerReleased;
            _settingsButton.PointerCanceled -= OnSettingsPointerExited;
            _settingsButton.PointerCaptureLost -= OnSettingsPointerExited;
            _settingsButton.Tapped -= OnSettingsTapped;
            _settingsButton.KeyDown -= OnSettingsKeyDown;
            _settingsButton.SizeChanged -= OnElementSizeChanged;
        }
    }

    #endregion

    #region Layout

    /// <summary>
    /// The number of cells the tile can land on: the modes, plus settings when it is shown.
    /// </summary>
    private int CellCount => _containers.Count + (IsSettingsVisible ? 1 : 0);

    /// <summary>
    /// Settings and the modes are one selection. Settings wins while it is set, so a host
    /// that leaves <see cref="SelectedIndex"/> pointing at a mode still shows settings lit.
    /// </summary>
    private int SelectedCellIndex
    {
        get
        {
            if (IsSettingsSelected && IsSettingsVisible)
                return _containers.Count;

            return SelectedIndex >= 0 && SelectedIndex < _containers.Count ? SelectedIndex : -1;
        }
    }

    /// <summary>
    /// Modes and settings are cells of the same grid, so they share the tile and the spacing
    /// rather than being laid out against each other.
    /// </summary>
    private void ApplyOrientation()
    {
        if (!_isTemplateApplied || _modeHost is null)
            return;

        var isVertical = Orientation == Orientation.Vertical;

        if (_settingsButton is not null)
        {
            _settingsButton.Visibility = IsSettingsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_settingsChip is not null)
        {
            _settingsChip.Visibility = IsSettingsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        _modeHost.ColumnDefinitions.Clear();
        _modeHost.RowDefinitions.Clear();
        _modeHost.RowSpacing = isVertical ? CellSpacing : 0d;
        _modeHost.ColumnSpacing = isVertical ? 0d : CellSpacing;

        var cellCount = Math.Max(CellCount, 1);

        for (var index = 0; index < cellCount; index++)
        {
            if (isVertical)
            {
                _modeHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                _modeHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        for (var index = 0; index < _containers.Count; index++)
        {
            SetCell(_containers[index], index, isVertical);

            if (index < _wells.Count)
            {
                SetCell(_wells[index], index, isVertical);
            }
        }

        if (IsSettingsVisible)
        {
            if (_settingsButton is not null)
            {
                SetCell(_settingsButton, _containers.Count, isVertical);
            }

            if (_settingsChip is not null)
            {
                SetCell(_settingsChip, _containers.Count, isVertical);
            }
        }

        if (_selectionIndicator is not null)
        {
            // The tile lives in the first cell and is moved by translation, so it always
            // measures to exactly one cell without anything having to compute its size.
            Grid.SetRow(_selectionIndicator, 0);
            Grid.SetColumn(_selectionIndicator, 0);
        }

        // Only the minor axis is fixed. The major axis is left to the host: horizontal cells
        // divide the pane's width between them, which is what makes the tile span a cell
        // rather than sit as a small square inside one.
        _modeHost.Height = isVertical
            ? (CellExtent * cellCount) + (CellSpacing * (cellCount - 1))
            : CellExtent;
        _modeHost.Width = isVertical ? CellExtent : double.NaN;
    }

    private static void SetCell(FrameworkElement element, int index, bool isVertical)
    {
        Grid.SetRow(element, isVertical ? index : 0);
        Grid.SetColumn(element, isVertical ? 0 : index);
    }

    private void OnModeHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A resize is not a selection change, so the tile is re-seated rather than slid.
        UpdateSelection(animate: false, popSelection: false);
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        // Scale animations pivot around this, so it has to follow the element's size.
        ElementCompositionPreview.GetElementVisual(element).CenterPoint =
            new Vector3((float)e.NewSize.Width / 2f, (float)e.NewSize.Height / 2f, 0f);
    }

    #endregion

    #region Selection

    private void UpdateSelection(bool animate) => UpdateSelection(animate, popSelection: animate);

    private void UpdateSelection(bool animate, bool popSelection)
    {
        if (!_isTemplateApplied || _modeHost is null)
            return;

        var selectedCell = SelectedCellIndex;
        var accent = AppModeGlyphPalette.ResolveAccent();
        var chip = ResolveChipBrush(accent);

        for (var index = 0; index < _containers.Count; index++)
        {
            var isSelected = index == selectedCell;

            ApplyItemForeground(index, isSelected, isPointerOver: false);
            ApplyChip(index, isSelected, chip);
        }

        ApplySettingsForeground();
        ApplySettingsChip(selectedCell, chip);
        ApplySelectionTile(accent);
        _ = UpdateGlyphsAsync(accent);

        if (_selectionIndicator is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(_selectionIndicator);

        if (selectedCell < 0)
        {
            // The tile keeps its position while it is hidden, so coming back to a cell fades
            // it in where that cell is instead of sliding it out of a stale one.
            SetOpacity(visual, 0f, animate);
            return;
        }

        MoveIndicator(visual, selectedCell, animate);
        SetOpacity(visual, 1f, animate);

        if (popSelection && AreAnimationsEnabled)
        {
            PlaySelectionPop(selectedCell);
        }
    }

    /// <summary>
    /// The chip is derived rather than themed: it is the accent pinned to a fixed weight, so
    /// it separates white artwork from the card without changing depth when the accent does.
    /// High contrast is the exception, where a derived colour would defeat the whole point of
    /// the mode and the system palette has to win.
    /// </summary>
    private Brush ResolveChipBrush(Windows.UI.Color accent)
    {
        if (RestingWellBrush is not null)
            return RestingWellBrush;

        if (TryGetHighContrastBrush("SystemColorButtonFaceColorBrush", out var systemBrush))
            return systemBrush;

        return new SolidColorBrush(AppModeGlyphPalette.ResolveRestingChip(accent, ActualTheme));
    }

    /// <summary>
    /// The tile is the chip taken darker, so it is derived from the accent for the same
    /// reason and left to the template only under high contrast.
    /// </summary>
    private void ApplySelectionTile(Windows.UI.Color accent)
    {
        if (_selectionIndicator is null || _accessibilitySettings.HighContrast)
            return;

        _selectionIndicator.Background =
            new SolidColorBrush(AppModeGlyphPalette.ResolveSelectionTile(accent, ActualTheme));
    }

    private bool TryGetHighContrastBrush(string key, out Brush brush)
    {
        brush = null!;

        if (!_accessibilitySettings.HighContrast)
            return false;

        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush found)
        {
            brush = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The glyph size in device pixels. The artwork is rasterised to exactly this, so it has
    /// to follow the display scale: a glyph rasterised for 100% and shown at 150% is a
    /// resampled bitmap, which is precisely how these icons lose their edges.
    /// </summary>
    private int GlyphPixelSize
        => (int)Math.Ceiling(GlyphExtent * (XamlRoot?.RasterizationScale ?? 1d));

    /// <summary>
    /// Recolours every glyph for the accent. Only the first build of a given accent and
    /// display scale does any work; the rest come from the cache, so selection and theme
    /// changes are free.
    /// </summary>
    private async Task UpdateGlyphsAsync(Windows.UI.Color accent)
    {
        var generation = ++_glyphGeneration;

        for (var index = 0; index < _containers.Count && index < Items.Count; index++)
        {
            var source = Items[index].GlyphSource;

            if (source is null || _containers[index].Child is not ImageIcon host)
                continue;

            var glyph = await AppModeGlyphPalette.CreateGlyphAsync(
                source,
                accent,
                AppModeGlyphPalette.Paper,
                GlyphPixelSize);

            // Another rebuild started while this one was awaiting, so its glyphs are the
            // current ones and these would land on top of them.
            if (generation != _glyphGeneration)
                return;

            if (glyph is not null)
            {
                host.Source = glyph;
            }
        }
    }

    private void MoveIndicator(Visual visual, int index, bool animate)
    {
        if (_modeHost is null)
            return;

        var isVertical = Orientation == Orientation.Vertical;
        var cellCount = Math.Max(CellCount, 1);

        // One cell plus the gap after it, which is the distance from a cell to the next.
        var spacing = isVertical ? _modeHost.RowSpacing : _modeHost.ColumnSpacing;
        var available = isVertical ? _modeHost.ActualHeight : _modeHost.ActualWidth;
        var extent = ((available - (spacing * (cellCount - 1))) / cellCount) + spacing;

        if (extent <= 0d)
            return;

        var distance = (float)(extent * index);
        var target = isVertical ? new Vector3(0f, distance, 0f) : new Vector3(distance, 0f, 0f);

        if (!animate || !AreAnimationsEnabled)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
            return;
        }

        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, target, CreateStandardEasing(compositor));
        animation.Duration = SlideDuration;

        visual.StartAnimation("Translation", animation);
    }

    private void SetOpacity(Visual visual, float opacity, bool animate)
    {
        if (!animate || !AreAnimationsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = opacity;
            return;
        }

        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, opacity, CreateStandardEasing(compositor));
        animation.Duration = FadeDuration;

        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>
    /// A short overshoot on the glyph that just became selected. Without it a switch reads as
    /// a repaint: the tile arrives, but nothing acknowledges the click itself.
    /// </summary>
    private void PlaySelectionPop(int cellIndex)
    {
        var host = cellIndex < _containers.Count
            ? _containers[cellIndex]
            : _settingsButton as FrameworkElement;

        var glyph = host switch
        {
            Border border => border.Child,
            Grid grid when grid.Children.Count > 0 => grid.Children[0],
            _ => null
        };

        if (host is null || glyph is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(glyph);
        var compositor = visual.Compositor;

        // The cell is star sized, so its declared size is NaN and only the arranged size
        // says where the middle is.
        visual.CenterPoint = new Vector3(
            (float)host.ActualWidth / 2f,
            (float)host.ActualHeight / 2f,
            0f);

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, Vector3.One);
        animation.InsertKeyFrame(0.5f, new Vector3(SelectionPopScale, SelectionPopScale, 1f));
        animation.InsertKeyFrame(1f, Vector3.One, CreateStandardEasing(compositor));
        animation.Duration = SelectionPopDuration;

        visual.StartAnimation("Scale", animation);
    }

    private void ApplyItemForeground(int index, bool isSelected, bool isPointerOver)
    {
        if (index < 0 || index >= _containers.Count)
            return;

        // Recoloured artwork carries its own colour; only a host-supplied icon takes one.
        if (_containers[index].Child is not IconElement icon || icon is ImageIcon)
            return;

        // Foregrounds are assigned rather than animated. A brush cannot be driven by the
        // Composition API without duplicating every glyph, and the motion already carries
        // the state change.
        var brush = isSelected
            ? ModeSelectedForeground
            : isPointerOver
                ? ModeHoverForeground
                : ModeForeground;

        if (brush is not null)
        {
            icon.Foreground = brush;
        }
    }

    /// <summary>
    /// Settings sits on the same chip as the modes, and hides it under the tile the same way.
    /// </summary>
    private void ApplySettingsChip(int selectedCell, Brush chip)
    {
        if (_settingsChip is null)
            return;

        _settingsChip.Background = chip;
        _settingsChip.Opacity = selectedCell == _containers.Count ? 0d : 1d;
    }

    private void ApplyChip(int index, bool isSelected, Brush chip)
    {
        if (index < 0 || index >= _wells.Count)
            return;

        _wells[index].Background = chip;
        _wells[index].Opacity = isSelected ? 0d : 1d;
    }

    #endregion

    #region Item interaction

    private void OnItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border container)
            return;

        ScaleTo(container, HoverScale);

        var index = (int)container.Tag;
        ApplyItemForeground(index, isSelected: index == SelectedCellIndex, isPointerOver: true);
    }

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border container)
            return;

        ScaleTo(container, 1f);

        var index = (int)container.Tag;
        ApplyItemForeground(index, isSelected: index == SelectedCellIndex, isPointerOver: false);
    }

    private void OnItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border container)
        {
            ScaleTo(container, PressedScale);
        }
    }

    private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border container)
        {
            ScaleTo(container, HoverScale);
        }
    }

    private void OnItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border container)
        {
            ModeInvoked?.Invoke(this, new WinoAppModeInvokedEventArgs((int)container.Tag));
        }
    }

    private void OnItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Border container || !IsInvocationKey(e.Key))
            return;

        ModeInvoked?.Invoke(this, new WinoAppModeInvokedEventArgs((int)container.Tag));
        e.Handled = true;
    }

    #endregion

    #region Settings interaction

    private void OnSettingsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetSettingsIconState(PointerOverIconState);
        ScaleSender(sender, HoverScale);
    }

    private void OnSettingsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetSettingsIconState(NormalIconState);
        ScaleSender(sender, 1f);
    }

    private void OnSettingsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetSettingsIconState(PressedIconState);
        ScaleSender(sender, PressedScale);
    }

    private void OnSettingsPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SetSettingsIconState(PointerOverIconState);
        ScaleSender(sender, HoverScale);
    }

    private void OnSettingsTapped(object sender, TappedRoutedEventArgs e)
        => SettingsInvoked?.Invoke(this, EventArgs.Empty);

    private void OnSettingsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsInvocationKey(e.Key))
            return;

        SettingsInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// <see cref="AnimatedIcon"/> normally takes its state from the visual states of the
    /// control hosting it. This control has none, so the states are set directly.
    /// </summary>
    private void SetSettingsIconState(string state)
    {
        if (_settingsIcon is null)
            return;

        AnimatedIcon.SetState(_settingsIcon, state);
    }

    private void ApplySettingsForeground()
    {
        if (_settingsIcon is null)
            return;

        var brush = IsSettingsSelected && IsSettingsVisible ? ModeSelectedForeground : SettingsForeground;

        if (brush is not null)
        {
            _settingsIcon.Foreground = brush;
        }
    }

    private void ApplySettingsLabel()
    {
        if (_settingsButton is null)
            return;

        AutomationProperties.SetName(_settingsButton, SettingsLabel);
        ToolTipService.SetToolTip(_settingsButton, string.IsNullOrEmpty(SettingsLabel) ? null : SettingsLabel);
    }

    #endregion

    #region Composition helpers

    private bool AreAnimationsEnabled => _uiSettings.AnimationsEnabled;

    private void ScaleSender(object sender, float scale)
    {
        if (sender is UIElement element)
        {
            ScaleTo(element, scale);
        }
    }

    private void ScaleTo(UIElement element, float scale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);

        if (!AreAnimationsEnabled)
        {
            visual.StopAnimation("Scale");
            visual.Scale = new Vector3(scale, scale, 1f);
            return;
        }

        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), CreateStandardEasing(compositor));
        animation.Duration = PointerScaleDuration;

        visual.StartAnimation("Scale", animation);
    }

    private static CompositionEasingFunction CreateStandardEasing(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    private static bool IsInvocationKey(VirtualKey key)
        => key is VirtualKey.Enter or VirtualKey.Space;

    #endregion
}
