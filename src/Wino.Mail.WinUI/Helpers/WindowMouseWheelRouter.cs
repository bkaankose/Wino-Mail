using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Subclasses top-level WinUI 3 HWNDs to ensure mouse wheel messages are reliably routed
/// to the element directly under the cursor (mail list, mail body, dialogs, etc.) regardless
/// of focus state or framework-level input bugs.
/// </summary>
public sealed class WindowMouseWheelRouter
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    private readonly IntPtr _hwnd;
    private readonly Window _window;
    private readonly FrameworkElement _rootElement;
    private readonly SubclassProc _subclassDelegate;
    private readonly UIntPtr _subclassId;
    private static int _nextSubclassId = 1000;

    private WindowMouseWheelRouter(Window window, FrameworkElement rootElement)
    {
        _window = window;
        _rootElement = rootElement;
        _hwnd = WindowNative.GetWindowHandle(window);
        _subclassId = new UIntPtr((uint)System.Threading.Interlocked.Increment(ref _nextSubclassId));
        _subclassDelegate = new SubclassProc(OnSubclassProc);

        SetWindowSubclass(_hwnd, _subclassDelegate, _subclassId, IntPtr.Zero);
        _window.Closed += OnWindowClosed;
    }

    public static WindowMouseWheelRouter Attach(Window window, FrameworkElement rootElement)
    {
        return new WindowMouseWheelRouter(window, rootElement);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        _window.Closed -= OnWindowClosed;
        RemoveWindowSubclass(_hwnd, _subclassDelegate, _subclassId);
    }

    private IntPtr OnSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_MOUSEWHEEL || uMsg == WM_MOUSEHWHEEL)
        {
            if (RouteMouseWheel(hWnd, uMsg, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private bool RouteMouseWheel(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        bool isHorizontal = (uMsg == WM_MOUSEHWHEEL);
        short delta = (short)((long)wParam >> 16);
        if (delta == 0) return false;

        // When Ctrl is held down, leave it to default zoom/pinch behaviors
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            return false;
        }

        bool isShiftPressed = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

        short screenX = (short)((long)lParam & 0xFFFF);
        short screenY = (short)((long)lParam >> 16);

        POINT pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(hWnd, ref pt);

        if (_rootElement.XamlRoot is null) return false;

        double scale = _rootElement.XamlRoot.RasterizationScale;
        if (scale <= 0) scale = 1.0;

        Point dipPoint = new Point(pt.X / scale, pt.Y / scale);

        // Standard Windows scroll delta: 60 DIPs per 120 notch
        double scrollPixels = (delta / 120.0) * 60.0;

        // 1. Check open popups (ContentDialogs, Flyouts)
        var openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(_rootElement.XamlRoot);
        if (openPopups is not null && openPopups.Count > 0)
        {
            for (int i = openPopups.Count - 1; i >= 0; i--)
            {
                var popup = openPopups[i];
                if (popup.Child is not null && popup.IsOpen)
                {
                    var popupHits = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, popup.Child);
                    if (TryScrollTarget(popupHits, isHorizontal, isShiftPressed, scrollPixels))
                    {
                        return true;
                    }
                }
            }
        }

        // 2. Check main window visual tree
        var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, _rootElement);
        if (TryScrollTarget(hitElements, isHorizontal, isShiftPressed, scrollPixels))
        {
            return true;
        }

        return false;
    }

    private static bool TryScrollTarget(IEnumerable<UIElement>? elements, bool isHorizontal, bool isShiftPressed, double scrollPixels)
    {
        if (elements is null) return false;

        foreach (var element in elements)
        {
            // 1. Check for WinoMailRenderer or WebView2
            if (element is Wino.Editor.WinoMailRenderer mailRenderer)
            {
                mailRenderer.ScrollBy(0, -(scrollPixels * 1.5));
                return true;
            }

            if (element is WebView2 webView)
            {
                if (webView.CoreWebView2 is not null)
                {
                    double deltaY = -(scrollPixels * 1.5);
                    _ = webView.ExecuteScriptAsync(
                        $"window.scrollBy({{ left: 0, top: {deltaY.ToString(CultureInfo.InvariantCulture)}, behavior: 'auto' }});");
                    return true;
                }
            }

            // 2. Check for ScrollViewer
            var scrollViewer = element as ScrollViewer ?? FindParent<ScrollViewer>(element);
            if (scrollViewer is not null)
            {
                if (!isHorizontal && !isShiftPressed)
                {
                    if (scrollViewer.ScrollableHeight > 0)
                    {
                        double current = scrollViewer.VerticalOffset;
                        double target = Math.Clamp(current - scrollPixels, 0, scrollViewer.ScrollableHeight);
                        if (Math.Abs(target - current) > 0.01)
                        {
                            scrollViewer.ChangeView(null, target, null, disableAnimation: false);
                            return true;
                        }
                    }
                }
                else
                {
                    if (scrollViewer.ScrollableWidth > 0)
                    {
                        double current = scrollViewer.HorizontalOffset;
                        double target = Math.Clamp(current - scrollPixels, 0, scrollViewer.ScrollableWidth);
                        if (Math.Abs(target - current) > 0.01)
                        {
                            scrollViewer.ChangeView(target, null, null, disableAnimation: false);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is T typed) return typed;
        }
        return null;
    }
}
