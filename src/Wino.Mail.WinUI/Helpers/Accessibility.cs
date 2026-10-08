using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Helpers;

/// <summary>
/// Gives native list containers the typed summary supplied by their data template.
/// Setting a name only on the template root leaves ListViewItem peers reading the model type.
/// </summary>
public static class Accessibility
{
    // Attached properties require static accessors, which the instance dependency-property
    // generator does not support.
    public static readonly DependencyProperty ContainerNameProperty = DependencyProperty.RegisterAttached(
        "ContainerName", typeof(string), typeof(Accessibility), new PropertyMetadata(null, OnContainerNameChanged));

    public static string GetContainerName(DependencyObject element) => (string)element.GetValue(ContainerNameProperty);

    public static void SetContainerName(DependencyObject element, string value) => element.SetValue(ContainerNameProperty, value);

    private static void OnContainerNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            element.Loaded -= OnTemplateLoaded;
            element.Loaded += OnTemplateLoaded;
            ApplyContainerName(element);
        }
    }

    private static void OnTemplateLoaded(object sender, RoutedEventArgs args) => ApplyContainerName((FrameworkElement)sender);

    private static void ApplyContainerName(FrameworkElement element)
    {
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ListViewItem or ListViewHeaderItem)
            {
                AutomationProperties.SetName(parent, GetContainerName(element) ?? string.Empty);
                return;
            }
        }
    }
}
