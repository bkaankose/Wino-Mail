#nullable enable

using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Behaviors;

/// <summary>
/// Turns any navigation item whose data context implements <see cref="IMenuItemDropTarget"/>
/// into a drop target. Lets pane templates declare drag and drop without code-behind, and
/// keeps the decision of what may be dropped where in the menu item itself.
/// </summary>
public static class MenuItemDropBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(MenuItemDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
            return;

        element.DragEnter -= OnDragEnter;
        element.DragLeave -= OnDragLeave;
        element.Drop -= OnDrop;

        if (args.NewValue is not true)
            return;

        element.AllowDrop = true;
        element.DragEnter += OnDragEnter;
        element.DragLeave += OnDragLeave;
        element.Drop += OnDrop;
    }

    private static void OnDragEnter(object sender, DragEventArgs args)
    {
        if (!TryGetTarget(sender, args, out var target, out var properties))
            return;

        target.IsDraggingItemOver = true;
        args.AcceptedOperation = DataPackageOperation.Move;
        args.DragUIOverride.Caption = target.GetDropCaption(properties);
        args.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: IMenuItemDropTarget target })
        {
            target.IsDraggingItemOver = false;
        }
    }

    private static async void OnDrop(object sender, DragEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: IMenuItemDropTarget hovered })
        {
            hovered.IsDraggingItemOver = false;
        }

        if (!TryGetTarget(sender, args, out var target, out var properties))
            return;

        args.AcceptedOperation = DataPackageOperation.Move;
        args.Handled = true;

        await target.HandleDropAsync(properties);
    }

    private static bool TryGetTarget(
        object sender,
        DragEventArgs args,
        out IMenuItemDropTarget target,
        out IReadOnlyDictionary<string, object> properties)
    {
        target = null!;
        properties = null!;

        if (sender is not FrameworkElement { DataContext: IMenuItemDropTarget dropTarget })
            return false;

        var snapshot = new Dictionary<string, object>();

        foreach (var pair in args.DataView.Properties)
        {
            snapshot[pair.Key] = pair.Value;
        }

        if (!dropTarget.CanAccept(snapshot))
            return false;

        target = dropTarget;
        properties = snapshot;
        return true;
    }
}
