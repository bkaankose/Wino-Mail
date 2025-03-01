using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.UWP.Views.Abstract;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Views;

public sealed partial class ManageAccountsPage : ManageAccountsPageAbstract,
    IRecipient<BackBreadcrumNavigationRequested>,
    IRecipient<BreadcrumbNavigationRequested>,
    IRecipient<MergedInboxRenamed>,
    IRecipient<AccountUpdatedMessage>
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = new ObservableCollection<BreadcrumbNavigationItemViewModel>();


    public ManageAccountsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuManageAccounts, WinoPage.AccountManagementPage);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));

        var accountManagementPageType = ViewModel.NavigationService.GetPageType(WinoPage.AccountManagementPage);

        AccountPagesFrame.Navigate(accountManagementPageType, null, new SuppressNavigationTransitionInfo());
    }


    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        var pageType = ViewModel.NavigationService.GetPageType(message.PageType);

        if (pageType == null) return;

        AccountPagesFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });

        PageHistory.ForEach(a => a.IsActive = false);

        PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
    }

    private void GoBackFrame()
    {
        if (AccountPagesFrame.CanGoBack)
        {
            PageHistory.RemoveAt(PageHistory.Count - 1);

            AccountPagesFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        }
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        var clickedPageHistory = PageHistory[args.Index];

        while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
        {
            AccountPagesFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
            PageHistory.RemoveAt(PageHistory.Count - 1);
            PageHistory[PageHistory.Count - 1].IsActive = true;
        }
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame();
    }

    public async void Receive(AccountUpdatedMessage message)
    {
        var activePage = PageHistory.FirstOrDefault(a => a.Request.PageType == WinoPage.AccountDetailsPage);

        if (activePage == null) return;

        await Dispatcher.TryRunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        {
            activePage.Title = message.Account.Name;
        });
    }

    public void Receive(MergedInboxRenamed message)
    {
        // TODO: Find better way to retrieve page history from the stack for the merged account.
        var activePage = PageHistory.LastOrDefault();

        if (activePage == null) return;

        activePage.Title = message.NewName;
    }
}
