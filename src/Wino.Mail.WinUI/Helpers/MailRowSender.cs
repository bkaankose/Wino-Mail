using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Wino.Helpers;

/// <summary>
/// Lets a thread head show another sender than the mail its body is bound to. The head and the first
/// child row share one mail item, so the override cannot live on the item: the head row template sets it
/// on the body presenter, and the shared body swaps its sender text for it.
/// </summary>
public static class MailRowSender
{
    // Attached properties require static accessors, which the instance dependency-property
    // generator does not support.
    public static readonly DependencyProperty OverrideProperty = DependencyProperty.RegisterAttached(
        "Override", typeof(string), typeof(MailRowSender), new PropertyMetadata(null, OnOverrideChanged));

    public static string GetOverride(DependencyObject element) => (string)element.GetValue(OverrideProperty);

    public static void SetOverride(DependencyObject element, string value) => element.SetValue(OverrideProperty, value);

    [DynamicWindowsRuntimeCast(typeof(ContentPresenter))]
    private static void OnOverrideChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ContentPresenter presenter) return;

        presenter.Loaded -= OnPresenterLoaded;
        presenter.Loaded += OnPresenterLoaded;
        Apply(presenter);
    }

    [DynamicWindowsRuntimeCast(typeof(ContentPresenter))]
    private static void OnPresenterLoaded(object sender, RoutedEventArgs args) => Apply((ContentPresenter)sender);

    /// <summary>Applies the override of the nearest body presenter above a body element that was just created.</summary>
    [DynamicWindowsRuntimeCast(typeof(ContentPresenter))]
    public static void ApplyFrom(DependencyObject bodyElement)
    {
        for (var parent = VisualTreeHelper.GetParent(bodyElement); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ContentPresenter presenter && presenter.ReadLocalValue(OverrideProperty) != DependencyProperty.UnsetValue)
            {
                Apply(presenter);
                return;
            }
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(TextBlock))]
    private static void Apply(ContentPresenter presenter)
    {
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0 ||
            VisualTreeHelper.GetChild(presenter, 0) is not FrameworkElement body)
        {
            return;
        }

        if (body.FindName("ThreadSenderText") is not TextBlock threadSender ||
            body.FindName("OwnSenderHost") is not FrameworkElement ownSender)
        {
            return;
        }

        var value = GetOverride(presenter);
        var hasOverride = !string.IsNullOrWhiteSpace(value);

        threadSender.Text = hasOverride ? value : string.Empty;
        threadSender.Visibility = hasOverride ? Visibility.Visible : Visibility.Collapsed;
        ownSender.Visibility = hasOverride ? Visibility.Collapsed : Visibility.Visible;
    }
}
