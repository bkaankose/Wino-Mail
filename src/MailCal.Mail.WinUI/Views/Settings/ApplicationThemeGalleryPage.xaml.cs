using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Personalization;
using MailCal.Helpers;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Views.Abstract;

namespace MailCal.Views.Settings;

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

        MailCalContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Edit,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Edit)),
                Command = ViewModel.EditThemeCommand,
                CommandParameter = theme,
                AutomationId = "ApplicationThemeGalleryEdit"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Delete,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Delete)),
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
