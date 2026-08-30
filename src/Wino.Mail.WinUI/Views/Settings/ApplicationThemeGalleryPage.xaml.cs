using Microsoft.UI.Xaml;
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

    private void ThemeCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: AppThemeBase theme } && !theme.IsCustomTheme)
            args.Handled = true;
    }
}
