using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wino.Core.Domain.Entities.Mail;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class MailCategoryManagementPage : MailCategoryManagementPageAbstract
{
    public MailCategoryManagementPage()
    {
        InitializeComponent();
    }

    private async void FavoriteCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton && toggleButton.Tag is MailCategory category)
        {
            await ViewModel.SetFavoriteAsync(category, true);
        }
    }

    private async void FavoriteCategoryUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton && toggleButton.Tag is MailCategory category)
        {
            await ViewModel.SetFavoriteAsync(category, false);
        }
    }

    private async void EditCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MailCategory category)
        {
            await ViewModel.EditCategoryAsync(category);
        }
    }

    private async void DeleteCategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MailCategory category)
        {
            await ViewModel.DeleteCategoryAsync(category);
        }
    }
}
