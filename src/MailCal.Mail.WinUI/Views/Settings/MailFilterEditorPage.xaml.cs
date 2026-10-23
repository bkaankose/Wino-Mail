using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using MailCal.Core.Domain.Enums;
using MailCal.Mail.ViewModels;
using MailCal.Views.Abstract;

namespace MailCal.Views.Settings;

public sealed partial class MailFilterEditorPage : MailFilterEditorPageAbstract
{
    public MailFilterEditorPage()
    {
        InitializeComponent();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(e.Parameter);
    }

    private void AddConditionClicked(object sender, RoutedEventArgs e)
        => ViewModel.AddCondition();

    private void RemoveConditionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterConditionEditorItem item })
            ViewModel.RemoveCondition(item);
    }

    private void AddActionClicked(object sender, RoutedEventArgs e)
        => ViewModel.AddAction();

    private void RemoveActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MailFilterActionEditorItem item })
            ViewModel.RemoveAction(item);
    }

    private void MailCalManagementClicked(object sender, RoutedEventArgs e)
        => ViewModel.SelectManagementType(MailFilterManagementType.MailCalLocal);

    private void ProviderManagementClicked(object sender, RoutedEventArgs e)
        => ViewModel.SelectManagementType(MailFilterManagementType.Provider);

    private void CancelClicked(object sender, RoutedEventArgs e)
        => ViewModel.NavigationService.GoBack();

    private async void SaveClicked(object sender, RoutedEventArgs e)
        => await ViewModel.SaveAsync();
}
