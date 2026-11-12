#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Models.Accounts;
using Wino.Helpers;
using Wino.Mail.WinUI.Helpers;
using WinRT;

namespace Wino.Mail.WinUI.Behaviors;

/// <summary>
/// Colors a text block or an icon with an account group color: the light value on a light surface and the dark value on a dark
/// one, following the element's actual theme. No key, or a Windows contrast theme, keeps the default text brush.
/// </summary>
public static class AccountGroupTextColor
{
    public static readonly DependencyProperty ColorKeyProperty =
        DependencyProperty.RegisterAttached(
            "ColorKey",
            typeof(string),
            typeof(AccountGroupTextColor),
            new PropertyMetadata(null, OnColorKeyChanged));

    public static readonly DependencyProperty IsThemeTrackedProperty =
        DependencyProperty.RegisterAttached(
            "IsThemeTracked",
            typeof(bool),
            typeof(AccountGroupTextColor),
            new PropertyMetadata(false));

    public static string? GetColorKey(DependencyObject element) => element.GetValue(ColorKeyProperty) as string;

    public static void SetColorKey(DependencyObject element, string? value) => element.SetValue(ColorKeyProperty, value);

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static void OnColorKeyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
            return;

        // The theme can change while the text block lives (system theme, Wino theme, contrast theme), and
        // XAML cannot subscribe an attached behavior to ActualThemeChanged, so it is tracked here once.
        if (element.GetValue(IsThemeTrackedProperty) is not true)
        {
            element.SetValue(IsThemeTrackedProperty, true);
            element.ActualThemeChanged += ElementActualThemeChanged;
        }

        Apply(element);
    }

    private static void ElementActualThemeChanged(FrameworkElement sender, object args) => Apply(sender);

    [DynamicWindowsRuntimeCast(typeof(TextBlock))]
    [DynamicWindowsRuntimeCast(typeof(IconElement))]
    private static void Apply(FrameworkElement element)
    {
        var foregroundProperty = element switch
        {
            TextBlock => TextBlock.ForegroundProperty,
            IconElement => IconElement.ForegroundProperty,
            _ => null
        };

        if (foregroundProperty == null)
            return;

        var hex = ContrastThemeHelper.IsActive
            ? null
            : AccountGroupColors.GetHex(GetColorKey(element), element.ActualTheme == ElementTheme.Dark);

        if (hex == null)
        {
            element.ClearValue(foregroundProperty);
            return;
        }

        element.SetValue(foregroundProperty, new SolidColorBrush(XamlHelpers.GetWindowsColorFromHex(hex)));
    }
}
