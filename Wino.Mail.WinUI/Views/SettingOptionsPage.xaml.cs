using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class SettingOptionsPage : SettingOptionsPageAbstract
{
    public SettingOptionsPage()
    {
        InitializeComponent();
    }

    private void SettingOptionClicked(object sender, RoutedEventArgs e)
    {
        WinoPage? page = sender switch
        {
            FrameworkElement element when element.Tag is WinoPage p => p,
            _ => null
        };

        if (page.HasValue)
        {
            ViewModel.NavigateSubDetailCommand.Execute(page.Value);
        }
    }

    private void AccountSettingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard settingsCard && settingsCard.CommandParameter is AccountProviderDetailViewModel account)
        {
            ViewModel.NavigateToAccount(account);
        }
    }

    private void MergedAccountSettingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard settingsCard && settingsCard.CommandParameter is MergedAccountProviderDetailViewModel account)
        {
            ViewModel.NavigateToAccount(account);
        }
    }

    private void AddAccountSettingClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToAddAccount();
    }

    private void ManageAccountsClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToManageAccounts();
    }

    private void SettingsSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput || string.IsNullOrWhiteSpace(sender.Text))
        {
            ViewModel.UpdateSearchSuggestions(sender.Text);
        }
    }

    private void SettingsSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsNavigationItemInfo selectedSetting)
        {
            ViewModel.SearchQuery = selectedSetting.Title;
        }
    }

    private void SettingsSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var selectedSetting = args.ChosenSuggestion as SettingsNavigationItemInfo
                              ?? ViewModel.GetBestSearchSuggestion(args.QueryText);

        ViewModel.NavigateToSetting(selectedSetting);
    }
}
