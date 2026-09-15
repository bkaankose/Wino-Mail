using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
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

    private void ThemeCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AppThemeBase theme } target)
            return;

        if (!theme.IsCustomTheme)
        {
            args.Handled = true;
            return;
        }

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Edit,
                Icon = new ContextFlyoutIcon("\uE70F"),
                Command = ViewModel.EditThemeCommand,
                CommandParameter = theme,
                AutomationId = "ApplicationThemeGalleryEdit"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Delete,
                Icon = new ContextFlyoutIcon("\uE74D"),
                Command = ViewModel.RemoveThemeCommand,
                CommandParameter = theme,
                IsDestructive = true,
                Shortcut = new ContextFlyoutShortcut("Delete", "Delete"),
                AutomationId = "ApplicationThemeGalleryDelete"
            }
        ]);
    }

    private void EditThemeClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: AppThemeBase theme } && ViewModel.EditThemeCommand.CanExecute(theme))
            ViewModel.EditThemeCommand.Execute(theme);
    }
}
