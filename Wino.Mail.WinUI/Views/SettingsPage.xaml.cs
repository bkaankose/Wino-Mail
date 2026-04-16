using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
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
    IRecipient<AccountUpdatedMessage>,
    ITitleBarSearchHost
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
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

        var initialPage = e.Parameter as WinoPage? ?? WinoPage.SettingOptionsPage;
        NavigateToRootPage(initialPage);
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
        WeakReferenceMessenger.Default.Register<AccountUpdatedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<SettingsRootNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Unregister<AccountUpdatedMessage>(this);
    }

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        NavigateBreadcrumb(message);
    }

    private void SettingsFrameNavigated(object sender, NavigationEventArgs e)
    {
        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
    }

    private void GoBackFrame(Core.Domain.Enums.NavigationTransitionEffect slideEffect)
    {
        if (!BreadcrumbNavigationHelper.GoBack(SettingsFrame, PageHistory, slideEffect))
            return;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        if (!BreadcrumbNavigationHelper.NavigateTo(SettingsFrame, PageHistory, args.Index))
            return;

        UpdateBackNavigationState();
        _ = RefreshCurrentPageStateAsync();
        UpdateWindowTitle();
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame(message.SlideEffect);
    }

    public void Receive(SettingsRootNavigationRequested message)
    {
        var currentRootPage = SettingsNavigationInfoProvider.GetRootPage(PageHistory.LastOrDefault()?.Request.PageType ?? WinoPage.SettingOptionsPage);
        if (message.PageType != WinoPage.SettingOptionsPage && currentRootPage == message.PageType)
            return;

        NavigateToRootPage(message.PageType);
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

    private void NavigateToRootPage(WinoPage targetPage)
    {
        PageHistory.Clear();
        SettingsFrame.BackStack.Clear();
        SettingsFrame.ForwardStack.Clear();

        NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage));

        if (targetPage != WinoPage.SettingOptionsPage)
        {
            NavigateBreadcrumb(new BreadcrumbNavigationRequested(
                SettingsNavigationInfoProvider.GetPageTitle(targetPage),
                targetPage));
            return;
        }

        UpdateWindowTitle();
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

    public bool CanNavigateBack => PageHistory.Count > 1 && SettingsFrame.CanGoBack;

    private async Task RefreshCurrentPageStateAsync()
    {
        var activePage = PageHistory.LastOrDefault()?.Request.PageType ?? WinoPage.SettingOptionsPage;
        var rootPage = SettingsNavigationInfoProvider.GetRootPage(activePage);
        await ViewModel.UpdateActivePageAsync(rootPage);
        WeakReferenceMessenger.Default.Send(new ActiveSettingsPageChanged(rootPage));
    }

    private void UpdateWindowTitle()
    {
        var activeTitle = PageHistory.LastOrDefault()?.Title;
        ViewModel.StatePersistenceService.CoreWindowTitle = string.IsNullOrWhiteSpace(activeTitle)
            ? Translator.MenuSettings
            : activeTitle;
    }

    private static string GetAccountDetailsTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account?.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account?.Name ?? Translator.AccountDetailsPage_Title;

    public Task OnTitleBarSearchTextChangedAsync()
    {
        SearchSuggestions.Clear();

        foreach (var item in SettingsNavigationInfoProvider.Search(SearchText, ViewModel.ManageAccountsDescription).Take(6))
        {
            SearchSuggestions.Add(new TitleBarSearchSuggestion(item.Title, item.Description, item));
        }

        return Task.CompletedTask;
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
        SearchText = suggestion.Title;
    }

    public Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = queryText;

        var selectedSetting = chosenSuggestion?.Tag as SettingsNavigationItemInfo
                              ?? SettingsNavigationInfoProvider.Search(queryText, ViewModel.ManageAccountsDescription).FirstOrDefault();

        if (selectedSetting?.PageType is WinoPage pageType)
        {
            Receive(new SettingsRootNavigationRequested(pageType));
        }

        return Task.CompletedTask;
    }
}
