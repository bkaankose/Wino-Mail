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
        frame.BackStack.Clear();
        frame.ForwardStack.Clear();
        frame.Content = null;
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
