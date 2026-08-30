using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Models;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;
using Wino.Views.Abstract;
using Wino.Views.Settings;

namespace Wino.Views;

public sealed partial class SettingsPage : SettingsPageAbstract, 
    IRecipient<BreadcrumbNavigationRequested>,
    IRecipient<BackBreadcrumNavigationRequested>,
    IRecipient<SettingsRootNavigationRequested>,
    IRecipient<MergedInboxRenamed>,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<AccountUpdatedMessage>,
    IInnerNavigationHost,
    ITitleBarSearchHost
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Settings;
    public string SearchText { get; set; } = string.Empty;
    public string SearchPlaceholderText => Translator.SettingsHome_SearchPlaceholder;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Register for frame navigation events to track back button visibility
        SettingsFrame.Navigated -= SettingsFrameNavigated;
        SettingsFrame.Navigated += SettingsFrameNavigated;

        var activationContext = e.Parameter as SettingsPageActivationContext;
        var initialPage = activationContext?.TargetPage
                          ?? e.Parameter as WinoPage?
                          ?? WinoPage.SettingOptionsPage;
        NavigateToRootPage(initialPage, activationContext?.PageParameter);
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        // Update Settings header in breadcrumb.

        var settingsHeader = PageHistory.FirstOrDefault();

        if (settingsHeader == null) return;

        settingsHeader.Title = Translator.MenuSettings;
        var manageAccountsEntry = PageHistory.FirstOrDefault(a =>
            a.Request.PageType == WinoPage.ManageAccountsPage || a.Request.PageType == WinoPage.AccountManagementPage);

        if (manageAccountsEntry != null)
        {
            manageAccountsEntry.Title = Translator.SettingsManageAccountSettings_Title;
        }

        var winoAccountEntry = PageHistory.FirstOrDefault(a => a.Request.PageType == WinoPage.WinoAccountManagementPage);

        if (winoAccountEntry != null)
        {
            winoAccountEntry.Title = Translator.WinoAccount_SettingsSection_Title;
        }

        var intelligenceEntry = PageHistory.FirstOrDefault(a => a.Request.PageType == WinoPage.WinoIntelligencePage);
        if (intelligenceEntry != null)
        {
            intelligenceEntry.Title = Translator.WinoIntelligence_SettingsTitle;
        }

        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        // Unregister frame navigation event
        SettingsFrame.Navigated -= SettingsFrameNavigated;

        base.OnNavigatingFrom(e);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<SettingsRootNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountRemovedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountUpdatedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<SettingsRootNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Unregister<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AccountRemovedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AccountUpdatedMessage>(this);
    }

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        NavigateBreadcrumb(message);
    }

    private void SettingsFrameNavigated(object sender, NavigationEventArgs e)
    {
        ContentScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task<bool> GoBackFrameAsync(
        Core.Domain.Enums.NavigationTransitionEffect slideEffect,
        NavigationResult? result = null,
        bool confirmationAlreadyHandled = false)
    {
        if (!confirmationAlreadyHandled &&
            SettingsFrame.Content is BasePage currentPage &&
            currentPage.AssociatedViewModel is Core.Domain.Interfaces.IConfirmBackNavigation confirmBackNavigation &&
            !await confirmBackNavigation.CanNavigateBackAsync())
        {
            return false;
        }

        result ??= (SettingsFrame.Content as BasePage)?.AssociatedViewModel is Core.Domain.Interfaces.IBreadcrumbNavigationResultProvider provider
            ? provider.TakeNavigationResult()
            : null;

        if (!BreadcrumbNavigationHelper.GoBack(SettingsFrame, PageHistory, slideEffect, result))
            return false;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
        return true;
    }

    private async void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        NavigationResult? result = null;
        if (SettingsFrame.Content is BasePage currentPage &&
            currentPage.AssociatedViewModel is Core.Domain.Interfaces.IConfirmBackNavigation confirmBackNavigation)
        {
            if (!await confirmBackNavigation.CanNavigateBackAsync())
                return;

            result = (currentPage.AssociatedViewModel as Core.Domain.Interfaces.IBreadcrumbNavigationResultProvider)?.TakeNavigationResult();
        }

        if (!BreadcrumbNavigationHelper.NavigateTo(SettingsFrame, PageHistory, args.Index, result))
            return;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        _ = GoBackFrameAsync(message.SlideEffect, message.Result, message.Result != null);
    }

    public void Receive(SettingsRootNavigationRequested message)
    {
        var activePage = PageHistory.LastOrDefault()?.Request.PageType ?? WinoPage.SettingOptionsPage;

        if (activePage == message.PageType)
            return;

        if (message.PageType == WinoPage.SettingOptionsPage)
        {
            NavigateToSettingsHome();
            return;
        }

        NavigateDirectlyToRootPage(message.PageType);
    }

    public void Receive(AccountUpdatedMessage message)
    {
        var activePage = PageHistory.LastOrDefault(a => a.Request.PageType == WinoPage.AccountDetailsPage);

        if (activePage == null)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            activePage.Title = GetAccountDetailsTitle(message.Account);
            _ = RefreshCurrentPageStateAsync();
            UpdateWindowTitle();
        });
    }

    public void Receive(AccountCreatedMessage message) => RefreshAfterAccountCollectionChanged();

    public void Receive(AccountRemovedMessage message) => RefreshAfterAccountCollectionChanged();

    private void RefreshAfterAccountCollectionChanged()
    {
        DispatcherQueue.TryEnqueue(() => _ = RefreshCurrentPageStateAsync());
    }

    public void Receive(MergedInboxRenamed message)
    {
        var activePage = PageHistory.LastOrDefault(a => a.Request.PageType == WinoPage.MergedAccountDetailsPage);

        if (activePage == null)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            activePage.Title = message.NewName;
            _ = RefreshCurrentPageStateAsync();
            UpdateWindowTitle();
        });
    }

    private void NavigateBreadcrumb(BreadcrumbNavigationRequested message)
    {
        if (!BreadcrumbNavigationHelper.Navigate(SettingsFrame, PageHistory, message, ViewModel.NavigationService.GetPageType))
            return;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    private void NavigateToRootPage(WinoPage targetPage, object? pageParameter = null)
    {
        if (targetPage == WinoPage.SettingOptionsPage)
        {
            NavigateToSettingsHome();
            UpdateWindowTitle();
            return;
        }

        NavigateDirectlyToRootPage(targetPage, pageParameter);
    }

    private void NavigateDirectlyToRootPage(WinoPage targetPage, object? pageParameter = null)
        => NavigateToRoute(new SettingsNavigationRoute(
        new[]
        {
            new SettingsNavigationRouteStep(
                SettingsNavigationInfoProvider.GetPageTitle(targetPage),
                targetPage,
                pageParameter)
        }));

    private void NavigateToRoute(SettingsNavigationRoute route)
    {
        if (route.Steps.Count == 0)
            return;

        var destination = route.Destination;
        var pageType = ViewModel.NavigationService.GetPageType(destination.PageType);

        if (pageType == null)
            return;

        SettingsFrame.BackStack.Clear();
        SettingsFrame.ForwardStack.Clear();

        if (!SettingsFrame.Navigate(pageType, destination.Parameter, new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            }))
        {
            return;
        }

        // Keep Settings home as the logical breadcrumb parent without constructing it first.
        SettingsFrame.BackStack.Clear();
        SettingsFrame.BackStack.Add(new PageStackEntry(
            typeof(SettingOptionsPage),
            null,
            new SuppressNavigationTransitionInfo()));

        foreach (var step in route.Steps.Take(route.Steps.Count - 1))
        {
            var stepPageType = ViewModel.NavigationService.GetPageType(step.PageType);

            if (stepPageType == null)
                return;

            SettingsFrame.BackStack.Add(new PageStackEntry(
                stepPageType,
                step.Parameter,
                new SuppressNavigationTransitionInfo()));
        }

        PageHistory.Clear();
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(
            new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage),
            isActive: false,
            stepNumber: 1,
            backStackDepth: 1));

        for (var index = 0; index < route.Steps.Count; index++)
        {
            var step = route.Steps[index];
            PageHistory.Add(new BreadcrumbNavigationItemViewModel(
                new BreadcrumbNavigationRequested(step.PageTitle, step.PageType, step.Parameter),
                isActive: index == route.Steps.Count - 1,
                stepNumber: index + 2,
                backStackDepth: index + 2));
        }

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    private void NavigateToSettingsHome()
    {
        if (PageHistory.Count == 0 || SettingsFrame.Content == null)
        {
            ResetToSettingsHome();
            return;
        }

        if (SettingsFrame.Content is SettingOptionsPage)
        {
            SetSettingsHomeHistory();
            return;
        }

        if (SettingsFrame.CanGoBack)
        {
            GoBackToExistingSettingsHome();
            return;
        }

        ResetToSettingsHome();
    }

    private void GoBackToExistingSettingsHome()
    {
        while (SettingsFrame.BackStack.Count > 1)
        {
            SettingsFrame.BackStack.RemoveAt(SettingsFrame.BackStack.Count - 1);
        }

        SettingsFrame.GoBack(new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        });

        SetSettingsHomeHistory();
        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    private void SetSettingsHomeHistory()
    {
        PageHistory.Clear();
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(
            new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage),
            isActive: true,
            stepNumber: 1,
            backStackDepth: SettingsFrame.BackStack.Count + 1));
    }

    private bool TryNavigateToBreadcrumbIndex(int targetIndex)
    {
        if (!BreadcrumbNavigationHelper.NavigateTo(SettingsFrame, PageHistory, targetIndex))
            return false;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
        return true;
    }

    private void ResetToSettingsHome()
    {
        PageHistory.Clear();
        SettingsFrame.BackStack.Clear();
        SettingsFrame.ForwardStack.Clear();

        NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage));
    }

    public void ResetForModeSwitch()
    {
        while (PageHistory.Count > 1 && SettingsFrame.CanGoBack)
        {
            if (!BreadcrumbNavigationHelper.GoBack(SettingsFrame, PageHistory, Core.Domain.Enums.NavigationTransitionEffect.FromRight))
                break;
        }

        SettingsFrame.ForwardStack.Clear();
        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    private void UpdateBackNavigationState()
    {
        WeakReferenceMessenger.Default.Send(new TitleBarShellContentUpdated());
    }

    /// <summary>
    /// Settings owns a breadcrumb frame of its own, so back navigation is consumed here
    /// instead of popping the inner shell frame.
    /// </summary>
    public bool CanNavigateBack => PageHistory.Count > 1 && SettingsFrame.CanGoBack;

    Task<bool> IInnerNavigationHost.NavigateBackAsync(Core.Domain.Enums.NavigationTransitionEffect effect)
        => GoBackFrameAsync(effect);

    private async Task RefreshCurrentPageStateAsync()
    {
        var activeEntry = PageHistory.LastOrDefault();
        var activePage = activeEntry?.Request.PageType ?? WinoPage.SettingOptionsPage;
        var rootPage = SettingsNavigationInfoProvider.GetRootPage(activePage);
        await ViewModel.UpdateActivePageAsync(activePage, activeEntry?.Request.Parameter, activeEntry?.Title);
        WeakReferenceMessenger.Default.Send(new ActiveSettingsPageChanged(rootPage));
    }

    private void UpdateWindowTitle()
    {
        var activeTitle = PageHistory.LastOrDefault()?.Title;
        ViewModel.StatePersistenceService.CoreWindowTitle = string.IsNullOrWhiteSpace(activeTitle) ||
                                                             string.Equals(activeTitle, Translator.MenuSettings, StringComparison.Ordinal)
            ? string.Empty
            : activeTitle;
    }

    private static string GetAccountDetailsTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account?.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account?.Name ?? Translator.AccountDetailsPage_Title;

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        var searchText = SearchText;
        var results = await ViewModel.SearchSettingsAsync(searchText);

        if (!string.Equals(SearchText, searchText, StringComparison.Ordinal))
            return;

        SearchSuggestions.Clear();

        foreach (var item in results.Take(6))
        {
            SearchSuggestions.Add(new TitleBarSearchSuggestion(item.Title, item.Description, item));
        }
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
        SearchText = suggestion.Title;
    }

    public async Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = queryText;

        var selectedSetting = chosenSuggestion?.Tag as SettingsNavigationItemInfo
                              ?? (await ViewModel.SearchSettingsAsync(queryText)).FirstOrDefault();

        if (selectedSetting?.NavigationRoute is SettingsNavigationRoute route)
            NavigateToRoute(route);
        else if (selectedSetting?.PageType is WinoPage pageType)
            Receive(new SettingsRootNavigationRequested(pageType));

    }
}
