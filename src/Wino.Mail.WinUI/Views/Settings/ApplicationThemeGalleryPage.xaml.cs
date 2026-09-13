using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class ApplicationThemeGalleryPage : ApplicationThemeGalleryPageAbstract
{
    public ApplicationThemeGalleryPage() => InitializeComponent();

    public static Visibility CustomVisibility(AppThemeBase theme)
        => theme?.AppThemeType == AppThemeType.Custom ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CompatibilityVisibility(AppThemeBase theme)
        => theme?.AppThemeType == AppThemeType.Custom ? Visibility.Collapsed : Visibility.Visible;

    public static string CompatibilityLabel(AppThemeBase theme) => theme?.Compatibility switch
    {
        ThemeCompatibility.Light => Translator.ApplicationThemeGallery_Light,
        ThemeCompatibility.Dark => Translator.ApplicationThemeGallery_Dark,
        _ => Translator.ApplicationThemeGallery_Both
    };

    private void ThemeGridItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is AppThemeBase theme && ViewModel.ApplyThemeCommand.CanExecute(theme))
            ViewModel.ApplyThemeCommand.Execute(theme);
    }

    private void ThemeCardLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: AppThemeBase theme } element && !theme.IsCustomTheme)
            element.ContextFlyout = null;
    }

    private void ThemeCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: AppThemeBase theme } && !theme.IsCustomTheme)
            args.Handled = true;
    }

    private void EditThemeClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: AppThemeBase theme } && ViewModel.EditThemeCommand.CanExecute(theme))
            ViewModel.EditThemeCommand.Execute(theme);
    }

    private void RemoveThemeClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: AppThemeBase theme } && ViewModel.RemoveThemeCommand.CanExecute(theme))
            ViewModel.RemoveThemeCommand.Execute(theme);
    }
}
