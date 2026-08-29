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
/// A strip of mutually exclusive modes with a separate settings affordance beside it.
///
/// The two halves are deliberately not peers: the modes live inside a card and share a
/// sliding selection tile, while settings sits outside the card as a quieter, smaller
/// button. Choosing a mode and opening settings are different kinds of action, and the
/// layout says so before anything is clicked.
///
/// The control carries no visual states. Every appearance change is applied from code and
/// every transition is a Composition animation, so there is no storyboard and no
/// <see cref="VisualStateManager"/> in the control or its template. The selection tile
/// slides on "Translation" rather than "Offset" because XAML owns an element's own offset
/// and would overwrite it on the next layout pass.
///
/// The control is domain agnostic: it never names mail, calendars, contacts or tasks. The
/// host fills <see cref="Items"/>, handles <see cref="ModeInvoked"/>, and decides whether
/// the selection actually moves.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public sealed partial class WinoAppModeSwitcher : Control
{
    private const string ModeCardPartName = "PART_ModeCard";
    private const string ModeHostPartName = "PART_ModeHost";
    private const string SelectionIndicatorPartName = "PART_SelectionIndicator";
    private const string SettingsButtonPartName = "PART_SettingsButton";
    private const string SettingsIconPartName = "PART_SettingsIcon";
    private const string RailSeparatorPartName = "PART_RailSeparator";

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
    /// The item footprint, shared by both orientations so the collapsed rail and the open
    /// pane present the same target size.
    /// </summary>
    private const double ItemExtent = 30d;

    /// <summary>
    /// The collapsed rail needs enough separation for its stacked app icons to scan as
    /// distinct destinations. Horizontal mode remains compact because it has more room.
    /// </summary>
    private const double VerticalItemSpacing = 4d;

    private readonly UISettings _uiSettings = new();
    private readonly List<Border> _containers = [];

    private Border? _modeCard;
    private Grid? _modeHost;
    private Border? _selectionIndicator;
    private Grid? _settingsButton;
    private AnimatedIcon? _settingsIcon;
    private Border? _railSeparator;
    private bool _isTemplateApplied;

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
    /// Accents the settings button. Independent of <see cref="SelectedIndex"/> because
    /// settings is not one of the modes.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSettingsSelected { get; set; }

    /// <summary>
    /// Tooltip and automation name for the settings button.
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
    /// A mode at rest.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeForeground { get; set; }

    /// <summary>
    /// A mode under the pointer.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeHoverForeground { get; set; }

    /// <summary>
    /// The selected mode, and the settings button while it owns the window.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? ModeSelectedForeground { get; set; }

    /// <summary>
    /// The settings button at rest. Quieter than a mode, because it is not one.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial Brush? SettingsForeground { get; set; }

    /// <summary>
    /// Raised when an item is clicked or activated from the keyboard.
    /// </summary>
    public event EventHandler<WinoAppModeInvokedEventArgs>? ModeInvoked;

    /// <summary>
    /// Raised when the settings button is clicked or activated from the keyboard.
    /// </summary>
    public event EventHandler? SettingsInvoked;

    public WinoAppModeSwitcher()
    {
        DefaultStyleKey = typeof(WinoAppModeSwitcher);

        Items.CollectionChanged += OnItemsChanged;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DetachTemplateParts();

        _modeCard = GetTemplateChild(ModeCardPartName) as Border;
        _modeHost = GetTemplateChild(ModeHostPartName) as Grid;
        _selectionIndicator = GetTemplateChild(SelectionIndicatorPartName) as Border;
        _settingsButton = GetTemplateChild(SettingsButtonPartName) as Grid;
        _settingsIcon = GetTemplateChild(SettingsIconPartName) as AnimatedIcon;
        _railSeparator = GetTemplateChild(RailSeparatorPartName) as Border;

        if (_modeHost is not null)
        {
            // The tile is sized from a cell, so a resize has to re-seat it even though no
            // selection changed.
            _modeHost.SizeChanged += OnModeHostSizeChanged;
        }

        if (_selectionIndicator is not null)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_selectionIndicator, true);
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
        ApplySettingsVisibility();
        ApplySettingsSelection();
        ApplySettingsLabel();

        // Arriving in a state is not a transition; the control simply shows what the host
        // already set.
        UpdateSelection(animate: false);
    }

    #region Property callbacks

    partial void OnSelectedIndexChanged(int newValue) => UpdateSelection(animate: true);

    partial void OnOrientationChanged(Orientation newValue)
    {
        ApplyOrientation();
        UpdateSelection(animate: false);
    }

    partial void OnIsSettingsSelectedChanged(bool newValue) => ApplySettingsSelection();

    partial void OnIsSettingsVisibleChanged(bool newValue) => ApplySettingsVisibility();

    partial void OnSettingsLabelChanged(string newValue) => ApplySettingsLabel();

    // A theme change swaps the brushes underneath, so the assigned foregrounds have to be
    // re-applied rather than left on the old theme's colours.
    partial void OnModeForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnModeHoverForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnModeSelectedForegroundChanged(Brush? newValue) => RefreshForegrounds();

    partial void OnSettingsForegroundChanged(Brush? newValue) => ApplySettingsSelection();

    private void RefreshForegrounds()
    {
        UpdateSelection(animate: false, popSelection: false);
        ApplySettingsSelection();
    }

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

        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];

            var container = new Border
            {
                Width = ItemExtent,
                Height = ItemExtent,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = true,
                UseSystemFocusVisuals = true,
                Tag = index,
                Child = item.Icon
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

    private void ClearContainers()
    {
        foreach (var container in _containers)
        {
            DetachContainer(container);
            _modeHost?.Children.Remove(container);
        }

        _containers.Clear();
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

        // The icon belongs to the item, not the container, so it has to be released before
        // the container goes away or the next rebuild cannot reparent it.
        container.Child = null;
    }

    private void DetachTemplateParts()
    {
        ClearContainers();

        if (_modeHost is not null)
        {
            _modeHost.SizeChanged -= OnModeHostSizeChanged;
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
    /// The strip runs along one axis and the settings button follows the card on that same
    /// axis, so the item host and the root grid are rebuilt together.
    /// </summary>
    private void ApplyOrientation()
    {
        if (!_isTemplateApplied || _modeHost is null)
            return;

        var isVertical = Orientation == Orientation.Vertical;

        _modeHost.ColumnDefinitions.Clear();
        _modeHost.RowDefinitions.Clear();
        _modeHost.RowSpacing = isVertical ? VerticalItemSpacing : 0d;

        var cellCount = Math.Max(_containers.Count, 1);

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
            var container = _containers[index];

            Grid.SetRow(container, isVertical ? index : 0);
            Grid.SetColumn(container, isVertical ? 0 : index);
        }

        if (_selectionIndicator is not null)
        {
            // The pill lives in the first cell and is moved by translation, so it always
            // measures to exactly one cell without anything having to compute its size.
            Grid.SetRow(_selectionIndicator, 0);
            Grid.SetColumn(_selectionIndicator, 0);
        }

        // A vertical rail stacks the items, so the host needs the height of the whole stack
        // rather than a single row.
        _modeHost.Height = isVertical
            ? (ItemExtent * cellCount) + (VerticalItemSpacing * (cellCount - 1))
            : double.NaN;
        _modeHost.Width = isVertical ? ItemExtent : double.NaN;

        ApplyRootLayout(isVertical);
        ApplySettingsVisibility();
    }

    private void ApplyRootLayout(bool isVertical)
    {
        if (_modeCard is null || _settingsButton is null || _railSeparator is null)
            return;

        if (_modeCard.Parent is not Grid root)
            return;

        root.ColumnDefinitions.Clear();
        root.RowDefinitions.Clear();

        if (isVertical)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            SetCell(_modeCard, 0, 0);
            SetCell(_railSeparator, 1, 0);
            SetCell(_settingsButton, 2, 0);
        }
        else
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            SetCell(_modeCard, 0, 0);
            SetCell(_railSeparator, 0, 1);
            SetCell(_settingsButton, 0, 2);
        }
    }

    private static void SetCell(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void OnModeHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A resize is not a selection change, so the tile is re-seated rather than slid.
        UpdateSelection(animate: false);
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

        var selectedIndex = SelectedIndex;
        var hasSelection = selectedIndex >= 0 && selectedIndex < _containers.Count;

        for (var index = 0; index < _containers.Count; index++)
        {
            ApplyItemForeground(index, isSelected: hasSelection && index == selectedIndex, isPointerOver: false);
        }

        if (_selectionIndicator is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(_selectionIndicator);

        if (!hasSelection)
        {
            // The tile keeps its position while it is hidden, so coming back to a mode fades
            // it in where that mode is instead of sliding it out of a stale cell.
            SetOpacity(visual, 0f, animate);
            return;
        }

        MoveIndicator(visual, selectedIndex, animate);
        SetOpacity(visual, 1f, animate);

        if (popSelection && AreAnimationsEnabled)
        {
            PlaySelectionPop(_containers[selectedIndex]);
        }
    }

    private void MoveIndicator(Visual visual, int index, bool animate)
    {
        if (_modeHost is null)
            return;

        var isVertical = Orientation == Orientation.Vertical;
        var cellCount = Math.Max(_containers.Count, 1);
        var extent = isVertical
            ? ((_modeHost.ActualHeight - (_modeHost.RowSpacing * (cellCount - 1))) / cellCount)
                + _modeHost.RowSpacing
            : _modeHost.ActualWidth / cellCount;

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
    /// A short overshoot on the icon that just became selected. Without it a mode switch
    /// reads as a repaint: the tile arrives, but nothing acknowledges the click itself.
    /// </summary>
    private static void PlaySelectionPop(Border container)
    {
        if (container.Child is not UIElement icon)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(icon);
        var compositor = visual.Compositor;

        visual.CenterPoint = new Vector3(
            (float)container.Width / 2f,
            (float)container.Height / 2f,
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

        if (_containers[index].Child is not IconElement icon)
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

    #endregion

    #region Item interaction

    private void OnItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border container)
            return;

        ScaleTo(container, HoverScale);

        var index = (int)container.Tag;
        ApplyItemForeground(index, isSelected: index == SelectedIndex, isPointerOver: true);
    }

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border container)
            return;

        ScaleTo(container, 1f);

        var index = (int)container.Tag;
        ApplyItemForeground(index, isSelected: index == SelectedIndex, isPointerOver: false);
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

    private void ApplySettingsSelection()
    {
        if (_settingsIcon is null)
            return;

        var brush = IsSettingsSelected ? ModeSelectedForeground : SettingsForeground;

        if (brush is not null)
        {
            _settingsIcon.Foreground = brush;
        }
    }

    private void ApplySettingsVisibility()
    {
        if (_settingsButton is not null)
        {
            _settingsButton.Visibility = IsSettingsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_railSeparator is not null)
        {
            // The separator only earns its place in the rail, where the gear sits under the
            // card instead of beside it.
            _railSeparator.Visibility = IsSettingsVisible && Orientation == Orientation.Vertical
                ? Visibility.Visible
                : Visibility.Collapsed;
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
