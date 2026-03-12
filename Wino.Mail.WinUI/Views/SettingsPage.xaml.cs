using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;
using Wino.Views.Abstract;
using Wino.Views.Settings;

namespace Wino.Views;

public sealed partial class SettingsPage : SettingsPageAbstract, 
    IRecipient<BreadcrumbNavigationRequested>,
    IRecipient<BackBreadcrumNavigationRequested>,
    IRecipient<MergedInboxRenamed>,
    IRecipient<AccountUpdatedMessage>
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];

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
        PageHistory.Clear();
        SettingsFrame.BackStack.Clear();
        SettingsFrame.ForwardStack.Clear();

        SettingsFrame.Navigate(typeof(SettingOptionsPage), null, new SuppressNavigationTransitionInfo());

        var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true, backStackDepth: SettingsFrame.BackStack.Count + 1));

        if (e.Parameter is WinoPage parameterPage)
        {
            switch (parameterPage)
            {
                case WinoPage.AppPreferencesPage:
                    NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.SettingsAppPreferences_Title, WinoPage.AppPreferencesPage));
                    break;
                case WinoPage.PersonalizationPage:
                    NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.SettingsPersonalization_Title, WinoPage.PersonalizationPage));
                    break;
                case WinoPage.StoragePage:
                    NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.SettingsStorage_Title, WinoPage.StoragePage));
                    break;
                case WinoPage.EmailTemplatesPage:
                    NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.SettingsEmailTemplates_Title, WinoPage.EmailTemplatesPage));
                    break;
                case WinoPage.ManageAccountsPage:
                case WinoPage.AccountManagementPage:
                    NavigateBreadcrumb(new BreadcrumbNavigationRequested(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage));
                    break;
            }
        }

        UpdateWindowTitle();
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

        UpdateWindowTitle();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        // Unregister frame navigation event
        SettingsFrame.Navigated -= SettingsFrameNavigated;

        // Reset navigation state when leaving SettingsPage
        ViewModel.StatePersistenceService.IsSettingsNavigating = false;

        base.OnNavigatingFrom(e);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Register<AccountUpdatedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<MergedInboxRenamed>(this);
        WeakReferenceMessenger.Default.Unregister<AccountUpdatedMessage>(this);
    }

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        NavigateBreadcrumb(message);
    }

    private void SettingsFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Update back button visibility based on whether we can go back within the frame
        ViewModel.StatePersistenceService.IsSettingsNavigating = SettingsFrame.CanGoBack;
    }

    private void GoBackFrame(Core.Domain.Enums.NavigationTransitionEffect slideEffect)
    {
        if (!BreadcrumbNavigationHelper.GoBack(SettingsFrame, PageHistory, slideEffect))
            return;

        ViewModel.StatePersistenceService.IsSettingsNavigating = SettingsFrame.CanGoBack;
        UpdateWindowTitle();
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        if (!BreadcrumbNavigationHelper.NavigateTo(SettingsFrame, PageHistory, args.Index))
            return;

        ViewModel.StatePersistenceService.IsSettingsNavigating = SettingsFrame.CanGoBack;
        UpdateWindowTitle();
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame(message.SlideEffect);
    }

    public void Receive(AccountUpdatedMessage message)
    {
        var activePage = PageHistory.LastOrDefault(a => a.Request.PageType == WinoPage.AccountDetailsPage);

        if (activePage == null)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            activePage.Title = message.Account.Name;
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
            UpdateWindowTitle();
        });
    }

    private void NavigateBreadcrumb(BreadcrumbNavigationRequested message)
    {
        if (!BreadcrumbNavigationHelper.Navigate(SettingsFrame, PageHistory, message, ViewModel.NavigationService.GetPageType))
            return;

        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var activeTitle = PageHistory.LastOrDefault()?.Title;
        ViewModel.StatePersistenceService.CoreWindowTitle = string.IsNullOrWhiteSpace(activeTitle)
            ? Translator.MenuSettings
            : activeTitle;
    }
}
