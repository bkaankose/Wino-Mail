using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

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
