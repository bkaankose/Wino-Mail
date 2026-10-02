using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Mail.WinUI.Helpers;

internal static class WindowCleanupHelper
{
    public static void CleanupFrame(Frame? frame)
    {
        if (frame == null)
            return;

        CleanupObject(frame.Content);
        ClearNavigationStack(frame);

        frame.Content = null;

        EvictCachedPages(frame);
    }

    /// <summary>
    /// The frame's page cache survives clearing the content and the back stack. Collapsing
    /// the cache size and restoring it drops pages cached as <c>NavigationCacheMode.Enabled</c>.
    /// It does not drop <c>Required</c> pages: the frame keeps those until it is destroyed
    /// itself, which is why no page under the shell is cached as Required.
    /// </summary>
    private static void EvictCachedPages(Frame frame)
    {
        var cacheSize = frame.CacheSize;

        frame.CacheSize = 0;
        frame.CacheSize = cacheSize;
    }

    public static void ClearNavigationStack(Frame? frame)
    {
        if (frame?.IsNavigationStackEnabled != true)
            return;

        if (frame.BackStack.Count > 0)
        {
            frame.BackStack.Clear();
        }

        if (frame.ForwardStack.Count > 0)
        {
            frame.ForwardStack.Clear();
        }
    }

    public static void CleanupObject(object? instance)
    {
        if (instance == null)
            return;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CleanupObject(instance, visited);
    }

    private static void CleanupObject(object? instance, HashSet<object> visited)
    {
        if (instance == null || !visited.Add(instance))
            return;

        switch (instance)
        {
            case Views.WinoAppShell shell:
                shell.PrepareForWindowClose();
                break;
            case Frame frame:
                CleanupFrame(frame);
                break;
            case BasePage page:
                page.PrepareForClose();
                break;
        }

        if (instance is DependencyObject dependencyObject)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(dependencyObject);
            for (int i = 0; i < childCount; i++)
            {
                CleanupObject(VisualTreeHelper.GetChild(dependencyObject, i), visited);
            }
        }

        if (instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
