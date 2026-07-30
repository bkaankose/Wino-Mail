using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.ViewModels;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class MailFiltersPage : MailFiltersPageAbstract
{
    public MailFiltersPage()
    {
        InitializeComponent();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(e.Parameter);
    }

    private void NewFilterClicked(object sender, RoutedEventArgs e)
        => ViewModel.CreateFilter();

    private async void RefreshClicked(object sender, RoutedEventArgs e)
        => await ViewModel.LoadAsync(ViewModel.Account?.Id);

    private void EditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterListItemViewModel item })
            ViewModel.EditFilter(item);
    }

    private void DuplicateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterListItemViewModel item })
            ViewModel.DuplicateFilter(item);
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterListItemViewModel item })
            await ViewModel.DeleteFilterAsync(item);
    }

    private async void MoveUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterListItemViewModel item })
            await ViewModel.MoveFilterAsync(item, -1);
    }

    private async void MoveDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterListItemViewModel item })
            await ViewModel.MoveFilterAsync(item, 1);
    }

    private async void EnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: MailFilterListItemViewModel item } toggle)
            await ViewModel.SetEnabledAsync(item, toggle.IsOn);
    }
}
