using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoreLinq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Abstract;
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

        // Re-register message handlers after base.OnNavigatedTo unregisters all handlers
        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Register<AccountUpdatedMessage>(this);

        // Register for frame navigation events to track back button visibility
        AccountPagesFrame.Navigated -= AccountPagesFrameNavigated;
        AccountPagesFrame.Navigated += AccountPagesFrameNavigated;

        var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuManageAccounts, WinoPage.AccountManagementPage);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));

        var accountManagementPageType = ViewModel.NavigationService.GetPageType(WinoPage.AccountManagementPage);

        AccountPagesFrame.Navigate(accountManagementPageType, null, new SuppressNavigationTransitionInfo());
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        // Explicitly unregister our message handlers before base.OnNavigatingFrom calls UnregisterAll
        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Unregister<AccountUpdatedMessage>(this);

        // Unregister frame navigation event
        AccountPagesFrame.Navigated -= AccountPagesFrameNavigated;

        // Reset navigation state when leaving ManageAccountsPage
        ViewModel.StatePersistenceService.IsManageAccountsNavigating = false;

        base.OnNavigatingFrom(e);
    }

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        var pageType = ViewModel.NavigationService.GetPageType(message.PageType);

        if (pageType == null) return;

        AccountPagesFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight });

        PageHistory.ForEach(a => a.IsActive = false);

        PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
    }

    private void AccountPagesFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Update back button visibility based on whether we can go back within the frame
        ViewModel.StatePersistenceService.IsManageAccountsNavigating = AccountPagesFrame.CanGoBack;
    }

    private void GoBackFrame(Core.Domain.Enums.NavigationTransitionEffect slideEffect)
    {
        if (AccountPagesFrame.CanGoBack)
        {
            PageHistory.RemoveAt(PageHistory.Count - 1);

            var winuiEffect = slideEffect switch
            {
                Core.Domain.Enums.NavigationTransitionEffect.FromLeft => Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromLeft,
                _ => Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight,
            };

            AccountPagesFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = winuiEffect });

            // Set the new last item as active
            if (PageHistory.Count > 0)
            {
                PageHistory.ForEach(a => a.IsActive = false);
                PageHistory[PageHistory.Count - 1].IsActive = true;
            }

            // Update back button visibility after navigation
            ViewModel.StatePersistenceService.IsManageAccountsNavigating = AccountPagesFrame.CanGoBack;
        }
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        var clickedPageHistory = PageHistory[args.Index];

        // Trigger GoBack repeatedly until we reach the clicked breadcrumb item
        while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
        {
            ViewModel.NavigationService.GoBack();
        }
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame(message.SlideEffect);
    }

    public void Receive(AccountUpdatedMessage message)
    {
        var activePage = PageHistory.FirstOrDefault(a => a.Request.PageType == WinoPage.AccountDetailsPage);

        if (activePage == null) return;

        DispatcherQueue.TryEnqueue(() =>
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
