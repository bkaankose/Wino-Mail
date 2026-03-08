using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Helpers;
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
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true, backStackDepth: AccountPagesFrame.BackStack.Count + 1));

        var accountManagementPageType = ViewModel.NavigationService.GetPageType(WinoPage.AccountManagementPage);

        AccountPagesFrame.Navigate(accountManagementPageType, null, new SuppressNavigationTransitionInfo());
        UpdateWindowTitle();
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
        BreadcrumbNavigationHelper.Navigate(AccountPagesFrame, PageHistory, message, ViewModel.NavigationService.GetPageType);
        UpdateWindowTitle();
    }

    private void AccountPagesFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Update back button visibility based on whether we can go back within the frame
        ViewModel.StatePersistenceService.IsManageAccountsNavigating = AccountPagesFrame.CanGoBack;
    }

    private void GoBackFrame(Core.Domain.Enums.NavigationTransitionEffect slideEffect)
    {
        if (!BreadcrumbNavigationHelper.GoBack(AccountPagesFrame, PageHistory, slideEffect))
            return;

        ViewModel.StatePersistenceService.IsManageAccountsNavigating = AccountPagesFrame.CanGoBack;
        UpdateWindowTitle();
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        if (!BreadcrumbNavigationHelper.NavigateTo(AccountPagesFrame, PageHistory, args.Index))
            return;

        ViewModel.StatePersistenceService.IsManageAccountsNavigating = AccountPagesFrame.CanGoBack;
        UpdateWindowTitle();
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
            UpdateWindowTitle();
        });
    }

    public void Receive(MergedInboxRenamed message)
    {
        // TODO: Find better way to retrieve page history from the stack for the merged account.
        var activePage = PageHistory.LastOrDefault();

        if (activePage == null) return;

        activePage.Title = message.NewName;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var activeTitle = PageHistory.LastOrDefault()?.Title;
        ViewModel.StatePersistenceService.CoreWindowTitle = string.IsNullOrWhiteSpace(activeTitle)
            ? Translator.MenuManageAccounts
            : activeTitle;
    }
}
