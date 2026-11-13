using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Interfaces;
using WinRT;

namespace Wino.Mail.WinUI.Controls;

public partial class WinoNavigationViewItem : NavigationViewItem
{
    [ThreadStatic]
    private static List<WinoNavigationViewItem>? t_newContainers;

    public WinoNavigationViewItem()
    {
        WatchForAbandonment(this);
    }

    /// <summary>
    /// Re-runs NavigationViewItem's visual state update. It has no public entry point;
    /// OnContentChanged runs it and is idempotent for unchanged content.
    /// </summary>
    internal void RefreshVisualState() => OnContentChanged(Content, Content);

    private const string PresenterPartName = "NavigationViewItemPresenter";
    private const string SelectionIndicatorName = "SelectionIndicator";

    /// <summary>
    /// NavigationView draws the selection indicator on a collapsed item whose child is selected. An item that only
    /// expands and collapses, such as an account group, sets this so it never looks selected.
    /// </summary>
    public bool IsSelectionIndicatorHidden
    {
        get { return (bool)GetValue(IsSelectionIndicatorHiddenProperty); }
        set { SetValue(IsSelectionIndicatorHiddenProperty, value); }
    }

    public static readonly DependencyProperty IsSelectionIndicatorHiddenProperty = DependencyProperty.Register(nameof(IsSelectionIndicatorHidden), typeof(bool), typeof(WinoNavigationViewItem), new PropertyMetadata(false, OnIsSelectionIndicatorHiddenChanged));

    private static void OnIsSelectionIndicatorHiddenChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is WinoNavigationViewItem control)
            control.HideSelectionIndicator();
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The presenter builds its own template later, so the indicator only exists once it loaded.
        if (GetTemplateChild(PresenterPartName) is FrameworkElement presenter)
        {
            presenter.Loaded -= PresenterLoadedForIndicator;
            presenter.Loaded += PresenterLoadedForIndicator;
        }

        HideSelectionIndicator();
    }

    private void PresenterLoadedForIndicator(object sender, RoutedEventArgs e) => HideSelectionIndicator();

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Shapes.Shape))]
    private void HideSelectionIndicator()
    {
        if (!IsSelectionIndicatorHidden ||
            GetTemplateChild(PresenterPartName) is not FrameworkElement presenter ||
            FindDescendant(presenter, SelectionIndicatorName) is not Microsoft.UI.Xaml.Shapes.Shape indicator)
        {
            return;
        }

        // The indicator's opacity and scale are animated by NavigationView, so only its fill stays put.
        indicator.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is FrameworkElement element && element.Name == name)
                return element;

            if (FindDescendant(child, name) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>
    /// Set on account group rows. Their menu item's IsExpanded is the expansion the user chose: WinoNavigationView
    /// shows it while the pane is open, and only a click, tap or Enter/Space on the row in the open pane writes the
    /// row's new state back. NavigationView's own changes (the closing pane, recycling, flyouts in the closed pane)
    /// never reach the item.
    /// </summary>
    public bool RemembersExpansion
    {
        get { return (bool)GetValue(RemembersExpansionProperty); }
        set { SetValue(RemembersExpansionProperty, value); }
    }

    public static readonly DependencyProperty RemembersExpansionProperty = DependencyProperty.Register(nameof(RemembersExpansion), typeof(bool), typeof(WinoNavigationViewItem), new PropertyMetadata(false, OnRemembersExpansionChanged));

    private bool _isExpansionInputAttached;

    private static void OnRemembersExpansionChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not WinoNavigationViewItem control || control._isExpansionInputAttached || !control.RemembersExpansion)
            return;

        // The expand chevron and NavigationView handle these events, so handled ones are observed too.
        control._isExpansionInputAttached = true;
        control.AddHandler(TappedEvent, new TappedEventHandler(control.ExpansionInputTapped), true);
        control.AddHandler(KeyUpEvent, new KeyEventHandler(control.ExpansionInputKeyUp), true);
    }

    private void ExpansionInputTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsOwnRowInput(e.OriginalSource))
            QueueExpansionCommit();
    }

    private void ExpansionInputKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space or Windows.System.VirtualKey.GamepadA && IsOwnRowInput(e.OriginalSource))
            QueueExpansionCommit();
    }

    /// <summary>Input on a child row bubbles through this row and does not toggle it.</summary>
    [DynamicWindowsRuntimeCast(typeof(NavigationViewItem))]
    private bool IsOwnRowInput(object? originalSource)
    {
        for (var element = originalSource as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is NavigationViewItem row)
                return ReferenceEquals(row, this);
        }

        return false;
    }

    /// <summary>Runs after NavigationView has applied the toggle that the input started.</summary>
    private void QueueExpansionCommit() => DispatcherQueue?.TryEnqueue(CommitExpansion);

    [DynamicWindowsRuntimeCast(typeof(NavigationView))]
    private void CommitExpansion()
    {
        if (!RemembersExpansion || DataContext is not IMenuItem menuItem || menuItem.IsExpanded == IsExpanded)
            return;

        for (var element = VisualTreeHelper.GetParent(this); element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is NavigationView navigationView)
            {
                if (navigationView.IsPaneOpen)
                    menuItem.IsExpanded = IsExpanded;

                return;
            }
        }
    }

    /// <summary>
    /// When positive, the item's info badge is shown only while the row is collapsed on screen, which includes every
    /// row in the closed pane. An account group uses it to stand in for the unread counts of its hidden account rows.
    /// </summary>
    public int CollapsedInfoBadgeCount
    {
        get { return (int)GetValue(CollapsedInfoBadgeCountProperty); }
        set { SetValue(CollapsedInfoBadgeCountProperty, value); }
    }

    public static readonly DependencyProperty CollapsedInfoBadgeCountProperty = DependencyProperty.Register(nameof(CollapsedInfoBadgeCount), typeof(int), typeof(WinoNavigationViewItem), new PropertyMetadata(0, OnCollapsedInfoBadgeCountChanged));

    private bool _isCollapsedInfoBadgeTracked;

    private static void OnCollapsedInfoBadgeCountChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not WinoNavigationViewItem control)
            return;

        if (!control._isCollapsedInfoBadgeTracked)
        {
            control._isCollapsedInfoBadgeTracked = true;
            control.RegisterPropertyChangedCallback(IsExpandedProperty, (_, _) => control.UpdateCollapsedInfoBadge());
            control.RegisterPropertyChangedCallback(InfoBadgeProperty, (_, _) => control.UpdateCollapsedInfoBadge());
        }

        control.UpdateCollapsedInfoBadge();
    }

    private void UpdateCollapsedInfoBadge()
    {
        if (InfoBadge is { } badge)
            badge.Visibility = !IsExpanded && CollapsedInfoBadgeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsDraggingItemOver
    {
        get { return (bool)GetValue(IsDraggingItemOverProperty); }
        set { SetValue(IsDraggingItemOverProperty, value); }
    }

    public static readonly DependencyProperty IsDraggingItemOverProperty = DependencyProperty.Register(nameof(IsDraggingItemOver), typeof(bool), typeof(WinoNavigationViewItem), new PropertyMetadata(false, OnIsDraggingItemOverChanged));

    private static void OnIsDraggingItemOverChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is WinoNavigationViewItem control)
            control.UpdateDragEnterState();
    }

    private void UpdateDragEnterState()
    {
        // TODO: Add animation. Maybe after overriding DragUI in shell?

        //if (IsDraggingItemOver)
        //{
        //    ScaleAnimation(new System.Numerics.Vector3(1.2f, 1.2f, 1.2f));
        //}
        //else
        //{
        //    ScaleAnimation(new System.Numerics.Vector3(1f, 1f, 1f));
        //}
    }

    [DynamicWindowsRuntimeCast(typeof(UIElement))]
    private void ScaleAnimation(Vector3 vector)
    {
        if (Content is UIElement content)
        {
            var visual = ElementCompositionPreview.GetElementVisual(content);
            visual.Scale = vector;
        }
    }

    /// <summary>
    /// NavigationView resolves a selection that sits inside a collapsed item by instantiating the
    /// templates of every child it walks past, and then drops those containers without recycling
    /// them. An element that came out of a DataTemplate is kept alive by XAML until it has had a
    /// parent, so each dropped container stayed in memory for good with its bindings still
    /// listening to the menu item. Every folder or settings selection added a few dozen of them.
    /// Containers that are still parentless once the current work has finished are given a parent
    /// for a moment, which is all XAML needs to let them go.
    /// </summary>
    private static void WatchForAbandonment(WinoNavigationViewItem container)
    {
        if (t_newContainers != null)
        {
            t_newContainers.Add(container);
            return;
        }

        var dispatcherQueue = container.DispatcherQueue;
        if (dispatcherQueue == null)
            return;

        t_newContainers = [container];

        if (!dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ReleaseAbandonedContainers))
        {
            t_newContainers = null;
        }
    }

    private static void ReleaseAbandonedContainers()
    {
        var containers = t_newContainers;
        t_newContainers = null;

        if (containers == null)
            return;

        ContentControl? temporaryParent = null;

        foreach (var container in containers)
        {
            if (container.Parent != null || VisualTreeHelper.GetParent(container) != null)
                continue;

            temporaryParent ??= new ContentControl();
            temporaryParent.Content = container;
            temporaryParent.Content = null;
        }
    }
}
