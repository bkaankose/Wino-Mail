using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using Wino.Mail.Controls.AccountIcon;
using WinRT;

namespace Wino.Mail.Controls.ModeSwitcher;

/// <summary>
/// Footer switcher between application modes. A floating card holds one glyph button per
/// visible item; an accent thumb springs between them on selection, and items that appear
/// or leave fold their width away while the others slide into place.
///
/// Horizontal, the unpinned items share the width and pinned ones keep a fixed slot behind
/// a divider. Vertical, for a compact pane, the card disappears and the same sequence
/// stacks as squares.
///
/// The control knows nothing about mail, calendars or settings: the host supplies the
/// items, maps <see cref="WinoModeSwitcherItem.Tag"/> back to whatever it navigates with,
/// and drives <see cref="SelectedItem"/> from its own state.
/// </summary>
public sealed partial class WinoModeSwitcher : UserControl
{
    private const double HorizontalItemHeight = 32;
    private const double PinnedItemWidth = 40;
    private const double VerticalItemSize = 40;
    private const float CardElevation = 12f;
    private const float ThumbElevation = 6f;
    private const string HorizontalLayoutStateName = "HorizontalLayout";
    private const string VerticalLayoutStateName = "VerticalLayout";

    private static readonly TimeSpan ThumbResizeDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ThumbFadeDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan ItemSlideDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ItemShowDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ItemHideDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan GlyphPopDuration = TimeSpan.FromMilliseconds(380);

    private readonly UISettings _uiSettings = new();
    private readonly List<ItemView> _views = [];
    private readonly Storyboard _thumbResizeStoryboard = new();
    private readonly DoubleAnimation _thumbWidthAnimation;
    private readonly DoubleAnimation _thumbHeightAnimation;

    private SpringVector3NaturalMotionAnimation? _thumbMoveAnimation;
    private Rect? _thumbTarget;
    private bool _isThumbShown;

    public ObservableCollection<WinoModeSwitcherItem> Items { get; } = [];

    /// <summary>
    /// The item the thumb rests on. Setting it moves the thumb without raising
    /// <see cref="ItemInvoked"/>, so a host can mirror its own navigation state here.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial WinoModeSwitcherItem? SelectedItem { get; set; }

    [GeneratedDependencyProperty(DefaultValue = Orientation.Horizontal)]
    public partial Orientation Orientation { get; set; }

    public event EventHandler<WinoModeSwitcherItemInvokedEventArgs>? ItemInvoked;

    public WinoModeSwitcher()
    {
        InitializeComponent();

        _thumbWidthAnimation = CreateThumbResizeAnimation("Width");
        _thumbHeightAnimation = CreateThumbResizeAnimation("Height");
        _thumbResizeStoryboard.Children.Add(_thumbWidthAnimation);
        _thumbResizeStoryboard.Children.Add(_thumbHeightAnimation);

        Items.CollectionChanged += ItemsCollectionChanged;
    }

    private bool AreAnimationsEnabled => _uiSettings.AnimationsEnabled;

    private DoubleAnimation CreateThumbResizeAnimation(string property)
    {
        var animation = new DoubleAnimation
        {
            Duration = new Duration(ThumbResizeDuration),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, Thumb);
        Storyboard.SetTargetProperty(animation, property);

        return animation;
    }

    partial void OnSelectedItemPropertyChanged(DependencyPropertyChangedEventArgs e) => ApplySelection(animated: true);

    partial void OnOrientationPropertyChanged(DependencyPropertyChangedEventArgs e) => ArrangeItems();

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        ItemsPanel.LayoutUpdated += ItemsPanelLayoutUpdated;

        ArrangeItems();
        ApplySelection(animated: false);
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        ItemsPanel.LayoutUpdated -= ItemsPanelLayoutUpdated;

        // Whatever the layout looks like when the control comes back, the thumb lands on
        // it directly instead of sliding in from where it was last seen.
        _thumbTarget = null;
    }

    #region Items

    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildViews();

    private void RebuildViews()
    {
        foreach (var view in _views)
        {
            DetachView(view);
            ItemsPanel.Children.Remove(view.Button);
        }

        _views.Clear();

        foreach (var item in Items)
        {
            var view = CreateView(item);
            _views.Add(view);
            ItemsPanel.Children.Add(view.Button);
        }

        ArrangeItems();
        ApplySelection(animated: false);
    }

    [DynamicWindowsRuntimeCast(typeof(Style))]
    private ItemView CreateView(WinoModeSwitcherItem item)
    {
        var icon = new WinoFontIcon { FontSize = 16 };

        var button = new ToggleButton
        {
            Style = (Style)Resources["WinoModeSwitcherItemStyle"],
            Content = icon,
        };

        button.Click += ItemClicked;
        button.Loaded += ItemLoaded;
        button.SizeChanged += ItemSizeChanged;

        var view = new ItemView(item, button, icon)
        {
            CallbackTokens =
            [
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.IsVisibleProperty, ItemLayoutPropertyChanged),
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.IsPinnedProperty, ItemLayoutPropertyChanged),
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.GlyphProperty, ItemGlyphPropertyChanged),
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.SelectedGlyphProperty, ItemGlyphPropertyChanged),
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.LabelProperty, ItemMetadataPropertyChanged),
                item.RegisterPropertyChangedCallback(WinoModeSwitcherItem.AutomationIdProperty, ItemMetadataPropertyChanged),
            ],
        };

        ApplyMetadata(view);
        ApplyGlyph(view, animated: false);

        return view;
    }

    private void DetachView(ItemView view)
    {
        view.Button.Click -= ItemClicked;
        view.Button.Loaded -= ItemLoaded;
        view.Button.SizeChanged -= ItemSizeChanged;

        var properties = new[]
        {
            WinoModeSwitcherItem.IsVisibleProperty,
            WinoModeSwitcherItem.IsPinnedProperty,
            WinoModeSwitcherItem.GlyphProperty,
            WinoModeSwitcherItem.SelectedGlyphProperty,
            WinoModeSwitcherItem.LabelProperty,
            WinoModeSwitcherItem.AutomationIdProperty,
        };

        for (var index = 0; index < properties.Length && index < view.CallbackTokens.Length; index++)
        {
            view.Item.UnregisterPropertyChangedCallback(properties[index], view.CallbackTokens[index]);
        }
    }

    private ItemView? FindView(object? sender)
        => _views.FirstOrDefault(view => ReferenceEquals(view.Button, sender) || ReferenceEquals(view.Item, sender));

    private ItemView? FindView(WinoModeSwitcherItem? item)
        => item is null ? null : _views.FirstOrDefault(view => ReferenceEquals(view.Item, item));

    private void ItemLayoutPropertyChanged(DependencyObject sender, DependencyProperty property) => ArrangeItems();

    private void ItemGlyphPropertyChanged(DependencyObject sender, DependencyProperty property)
    {
        if (FindView(sender) is { } view)
        {
            ApplyGlyph(view, animated: false);
        }
    }

    private void ItemMetadataPropertyChanged(DependencyObject sender, DependencyProperty property)
    {
        if (FindView(sender) is { } view)
        {
            ApplyMetadata(view);
        }
    }

    private static void ApplyMetadata(ItemView view)
    {
        var label = view.Item.Label;

        AutomationProperties.SetName(view.Button, label);
        ToolTipService.SetToolTip(view.Button, string.IsNullOrEmpty(label) ? null : label);

        if (!string.IsNullOrEmpty(view.Item.AutomationId))
        {
            AutomationProperties.SetAutomationId(view.Button, view.Item.AutomationId);
        }
    }

    private void ApplyGlyph(ItemView view, bool animated)
    {
        var isSelected = ReferenceEquals(view.Item, SelectedItem);
        var selectedGlyph = string.IsNullOrEmpty(view.Item.SelectedGlyph) ? view.Item.Glyph : view.Item.SelectedGlyph;
        var glyph = isSelected ? selectedGlyph : view.Item.Glyph;
        var changed = view.Icon.Glyph != glyph;

        view.Icon.Glyph = glyph;

        if (isSelected && changed && animated && AreAnimationsEnabled)
        {
            PlayGlyphPop(view.Icon);
        }
    }

    #endregion

    #region Layout

    /// <summary>
    /// Rebuilds the panel definitions for the visible sequence: unpinned items, the
    /// divider, then pinned items. Hidden buttons stay in the panel, collapsed, so their
    /// hide animation can play from where they were.
    /// </summary>
    private void ArrangeItems()
    {
        var isVertical = Orientation == Orientation.Vertical;

        VisualStateManager.GoToState(this, isVertical ? VerticalLayoutStateName : HorizontalLayoutStateName, false);
        Card.Translation = new Vector3(0, 0, isVertical ? 0 : CardElevation);

        var unpinned = _views.Where(view => view.Item.IsVisible && !view.Item.IsPinned).ToList();
        var pinned = _views.Where(view => view.Item.IsVisible && view.Item.IsPinned).ToList();
        var showDivider = unpinned.Count > 0 && pinned.Count > 0;

        ItemsPanel.ColumnDefinitions.Clear();
        ItemsPanel.RowDefinitions.Clear();

        var slot = 0;

        foreach (var view in unpinned)
        {
            PlaceInSlot(view.Button, slot++, isVertical, isPinned: false);
        }

        if (showDivider)
        {
            AddDefinition(isVertical, GridLength.Auto);
            PlaceElement(Divider, slot++, isVertical);
        }

        foreach (var view in pinned)
        {
            PlaceInSlot(view.Button, slot++, isVertical, isPinned: true);
        }

        foreach (var view in _views)
        {
            var isVisible = view.Item.IsVisible;
            view.Button.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

            if (!isVisible)
            {
                PlaceElement(view.Button, 0, isVertical);
            }
        }

        Divider.Visibility = showDivider ? Visibility.Visible : Visibility.Collapsed;

        if (isVertical)
        {
            Divider.ClearValue(WidthProperty);
            Divider.Height = 1;
            Divider.Margin = new Thickness(8, 5, 8, 5);
            Divider.HorizontalAlignment = HorizontalAlignment.Stretch;
            Divider.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            Divider.ClearValue(HeightProperty);
            Divider.Width = 1;
            Divider.Margin = new Thickness(4, 5, 4, 5);
            Divider.HorizontalAlignment = HorizontalAlignment.Center;
            Divider.VerticalAlignment = VerticalAlignment.Stretch;
        }

        var slotCount = Math.Max(slot, 1);
        Grid.SetColumnSpan(Thumb, isVertical ? 1 : slotCount);
        Grid.SetRowSpan(Thumb, isVertical ? slotCount : 1);
    }

    private void PlaceInSlot(ToggleButton button, int slot, bool isVertical, bool isPinned)
    {
        if (isVertical)
        {
            AddDefinition(isVertical, GridLength.Auto);
            button.Width = VerticalItemSize;
            button.Height = VerticalItemSize;
            button.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            AddDefinition(isVertical, isPinned ? GridLength.Auto : new GridLength(1, GridUnitType.Star));
            button.Height = HorizontalItemHeight;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;

            if (isPinned)
            {
                button.Width = PinnedItemWidth;
            }
            else
            {
                button.ClearValue(WidthProperty);
            }
        }

        PlaceElement(button, slot, isVertical);
    }

    private void AddDefinition(bool isVertical, GridLength length)
    {
        if (isVertical)
        {
            ItemsPanel.RowDefinitions.Add(new RowDefinition { Height = length });
        }
        else
        {
            ItemsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
        }
    }

    private static void PlaceElement(FrameworkElement element, int slot, bool isVertical)
    {
        Grid.SetColumn(element, isVertical ? 0 : slot);
        Grid.SetRow(element, isVertical ? slot : 0);
    }

    private void ItemsPanelLayoutUpdated(object? sender, object e) => UpdateThumb();

    private void ItemSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FindView(sender) is { } view)
        {
            // Scale animations grow from the middle of the button, not its corner. The
            // buttons are driven through their composition visual only: touching the
            // UIElement.CenterPoint property instead would lock GetElementVisual out.
            ElementCompositionPreview.GetElementVisual(view.Button).CenterPoint =
                new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        }
    }

    #endregion

    #region Selection

    private void ItemClicked(object sender, RoutedEventArgs e)
    {
        if (FindView(sender) is not { } view)
            return;

        if (ReferenceEquals(view.Item, SelectedItem))
        {
            // A toggle button unchecks itself on click; the selected mode stays selected.
            view.Button.IsChecked = true;
            return;
        }

        Invoke(view.Item);
    }

    private void ItemsPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var visible = _views.Where(view => view.Item.IsVisible).ToList();

        if (visible.Count == 0)
            return;

        var current = visible.FindIndex(view => ReferenceEquals(view.Item, SelectedItem));

        var next = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => current <= 0 ? visible.Count - 1 : current - 1,
            VirtualKey.Right or VirtualKey.Down => (current + 1) % visible.Count,
            VirtualKey.Home => 0,
            VirtualKey.End => visible.Count - 1,
            _ => -1,
        };

        if (next < 0)
            return;

        e.Handled = true;

        var view = visible[next];

        if (!ReferenceEquals(view.Item, SelectedItem))
        {
            Invoke(view.Item);
        }

        view.Button.Focus(FocusState.Keyboard);
    }

    private void Invoke(WinoModeSwitcherItem item)
    {
        SelectedItem = item;
        ItemInvoked?.Invoke(this, new WinoModeSwitcherItemInvokedEventArgs(item));
    }

    private void ApplySelection(bool animated)
    {
        foreach (var view in _views)
        {
            view.Button.IsChecked = ReferenceEquals(view.Item, SelectedItem);
            ApplyGlyph(view, animated);
        }

        UpdateThumb();
    }

    #endregion

    #region Thumb

    /// <summary>
    /// Runs after every layout pass and only acts when the selected button's slot has
    /// actually moved or resized, so the thumb follows selection changes, items folding
    /// away, orientation changes and pane resizes through the same path.
    /// </summary>
    private void UpdateThumb()
    {
        var view = FindView(SelectedItem);

        if (view is null || !view.Item.IsVisible || view.Button.ActualWidth <= 0 || !IsLoaded)
        {
            HideThumb();
            return;
        }

        var origin = view.Button.TransformToVisual(ItemsPanel).TransformPoint(new Point(0, 0));
        var target = new Rect(origin.X, origin.Y, view.Button.ActualWidth, view.Button.ActualHeight);

        if (_thumbTarget is { } previous && AreClose(previous, target))
            return;

        var animate = _thumbTarget is not null && _isThumbShown && AreAnimationsEnabled;
        _thumbTarget = target;

        MoveThumb(target, animate);
        ShowThumb();
    }

    private static bool AreClose(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private void MoveThumb(Rect target, bool animate)
    {
        var translation = new Vector3((float)target.X, (float)target.Y, ThumbElevation);

        if (_thumbMoveAnimation is not null)
        {
            Thumb.StopAnimation(_thumbMoveAnimation);
            _thumbMoveAnimation = null;
        }

        if (!animate)
        {
            _thumbResizeStoryboard.Stop();
            Thumb.Width = target.Width;
            Thumb.Height = target.Height;
            Thumb.Translation = translation;
            return;
        }

        _thumbWidthAnimation.To = target.Width;
        _thumbHeightAnimation.To = target.Height;
        _thumbResizeStoryboard.Begin();

        var spring = GetCompositor().CreateSpringVector3Animation();
        spring.Target = "Translation";
        spring.FinalValue = translation;
        spring.DampingRatio = 0.72f;
        spring.Period = TimeSpan.FromMilliseconds(55);

        _thumbMoveAnimation = spring;
        Thumb.StartAnimation(spring);
    }

    private void ShowThumb()
    {
        if (_isThumbShown)
            return;

        _isThumbShown = true;
        FadeThumb(1f);
    }

    private void HideThumb()
    {
        _thumbTarget = null;

        if (!_isThumbShown)
            return;

        _isThumbShown = false;
        FadeThumb(0f);
    }

    private void FadeThumb(float opacity)
    {
        if (!AreAnimationsEnabled)
        {
            Thumb.Opacity = opacity;
            return;
        }

        var compositor = GetCompositor();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";
        animation.InsertKeyFrame(1f, opacity, CreateStandardEasing(compositor));
        animation.Duration = ThumbFadeDuration;

        Thumb.StartAnimation(animation);
    }

    #endregion

    #region Item animations

    /// <summary>
    /// Implicit animations attach once the button has its first layout, so items slide to
    /// new slots and fade in or out from then on, but never animate in from the origin.
    /// </summary>
    private void ItemLoaded(object sender, RoutedEventArgs e)
    {
        if (FindView(sender) is not { } view || !AreAnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(view.Button);

        if (visual.ImplicitAnimations is not null)
            return;

        var compositor = visual.Compositor;
        var easing = CreateStandardEasing(compositor);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Offset";
        slide.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        slide.Duration = ItemSlideDuration;

        var implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Offset"] = slide;
        visual.ImplicitAnimations = implicitAnimations;

        var show = compositor.CreateAnimationGroup();
        show.Add(CreateOpacityAnimation(compositor, 0f, 1f, ItemShowDuration, easing));
        show.Add(CreateScaleAnimation(compositor, 0.6f, 1f, ItemShowDuration, easing));
        ElementCompositionPreview.SetImplicitShowAnimation(view.Button, show);

        var hide = compositor.CreateAnimationGroup();
        hide.Add(CreateOpacityAnimation(compositor, null, 0f, ItemHideDuration, easing));
        hide.Add(CreateScaleAnimation(compositor, null, 0.6f, ItemHideDuration, easing));
        ElementCompositionPreview.SetImplicitHideAnimation(view.Button, hide);
    }

    private void PlayGlyphPop(WinoFontIcon icon)
    {
        if (icon.ActualWidth <= 0 || icon.ActualHeight <= 0)
            return;

        icon.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0);

        var compositor = GetCompositor();
        var pop = compositor.CreateVector3KeyFrameAnimation();
        pop.Target = "Scale";
        pop.InsertKeyFrame(0f, Vector3.One);
        pop.InsertKeyFrame(0.3f, new Vector3(0.8f, 0.8f, 1f), compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.8f, 0.6f)));
        pop.InsertKeyFrame(0.65f, new Vector3(1.12f, 1.12f, 1f), CreateStandardEasing(compositor));
        pop.InsertKeyFrame(1f, Vector3.One, CreateStandardEasing(compositor));
        pop.Duration = GlyphPopDuration;

        icon.StartAnimation(pop);
    }

    private static ScalarKeyFrameAnimation CreateOpacityAnimation(
        Compositor compositor,
        float? from,
        float to,
        TimeSpan duration,
        CompositionEasingFunction easing)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";

        if (from.HasValue)
        {
            animation.InsertKeyFrame(0f, from.Value);
        }

        animation.InsertKeyFrame(1f, to, easing);
        animation.Duration = duration;

        return animation;
    }

    private static Vector3KeyFrameAnimation CreateScaleAnimation(
        Compositor compositor,
        float? from,
        float to,
        TimeSpan duration,
        CompositionEasingFunction easing)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";

        if (from.HasValue)
        {
            animation.InsertKeyFrame(0f, new Vector3(from.Value, from.Value, 1f));
        }

        animation.InsertKeyFrame(1f, new Vector3(to, to, 1f), easing);
        animation.Duration = duration;

        return animation;
    }

    private static CompositionEasingFunction CreateStandardEasing(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    private Compositor GetCompositor() => ElementCompositionPreview.GetElementVisual(this).Compositor;

    #endregion

    private sealed class ItemView(WinoModeSwitcherItem item, ToggleButton button, WinoFontIcon icon)
    {
        public WinoModeSwitcherItem Item { get; } = item;

        public ToggleButton Button { get; } = button;

        public WinoFontIcon Icon { get; } = icon;

        public long[] CallbackTokens { get; init; } = [];
    }
}
