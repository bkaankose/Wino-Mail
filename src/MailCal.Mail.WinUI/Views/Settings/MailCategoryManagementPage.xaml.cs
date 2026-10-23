using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Views.Abstract;
using WinRT;

namespace MailCal.Views.Settings;

public sealed partial class MailCategoryManagementPage : MailCategoryManagementPageAbstract
{
    public MailCategoryManagementPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleButton))]
    private async void FavoriteCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton && toggleButton.Tag is MailCategory category)
        {
            await ViewModel.SetFavoriteAsync(category, true);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleButton))]
    private async void FavoriteCategoryUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton && toggleButton.Tag is MailCategory category)
        {
            await ViewModel.SetFavoriteAsync(category, false);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private async void EditCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MailCategory category)
        {
            await ViewModel.EditCategoryAsync(category);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private async void DeleteCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MailCategory category)
        {
            await ViewModel.DeleteCategoryAsync(category);
        }
    }
}
