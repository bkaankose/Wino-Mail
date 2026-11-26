using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Shell.Contracts;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.Common;

public partial class SettingsPageViewModel : CoreBaseViewModel, IShellMenuOwner
{
    private readonly IAccountService _accountService;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IMailProviderRegistry _providers;
    private IReadOnlyList<SettingsNavigationItemInfo> _accountSearchItems = [];
    private bool _isAccountSearchIndexInitialized;
    private int _activePageGeneration;

    private readonly SettingsMenuProvider _settingsMenuProvider;

    public SettingsPageViewModel(
        IMessenger messenger,
        INavigationService navigationService,
        IStatePersistanceService statePersistenceService,
        IAccountService accountService,
        IWinoAccountIntelligenceSnapshotService entitlementService,
        SettingsMenuProvider settingsMenuProvider,
        IMailProviderRegistry providers) : base(messenger)
    {
        _providers = providers;
        NavigationService = navigationService;
        StatePersistenceService = statePersistenceService;
        _accountService = accountService;
        _entitlementService = entitlementService;
        ShellMenuProvider = settingsMenuProvider;
        _settingsMenuProvider = settingsMenuProvider;
    }

    /// <summary>
    /// The settings pane belongs to the settings mode provider; the page just hands it to
    /// the shell when it is navigated to.
    /// </summary>
    public IShellMenuProvider ShellMenuProvider { get; }

    public INavigationService NavigationService { get; }
    public IStatePersistanceService StatePersistenceService { get; }

    [ObservableProperty]
    public partial string CurrentDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManageAccountsDescription { get; set; } = string.Empty;

    public async Task UpdateActivePageAsync(WinoPage pageType, object parameter = null, string pageTitle = null)
    {
        var generation = Interlocked.Increment(ref _activePageGeneration);
        await RefreshAccountSummaryAsync();

        // Navigations that follow each other quickly can finish out of order; only the latest page sets the description.
        var description = await GetPageDescriptionAsync(pageType, parameter, pageTitle).ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            if (generation == Volatile.Read(ref _activePageGeneration))
                CurrentDescription = description;
        });
    }

    public async Task<IReadOnlyList<SettingsNavigationItemInfo>> SearchSettingsAsync(string query)
    {
        if (!_isAccountSearchIndexInitialized)
            await RefreshAccountSummaryAsync().ConfigureAwait(false);

        var results = SettingsNavigationInfoProvider.Search(query, ManageAccountsDescription, _accountSearchItems)
            .Where(item => item.PageType is not { } page || _settingsMenuProvider.IsPageAvailable(page))
            .ToArray();
        return _entitlementService.CurrentEntitlement.CanAccessSurfaces
            ? results
            : results.Where(item => item.PageType != WinoPage.WinoIntelligenceManagementPage &&
                                    item.PageType != WinoPage.IntelligenceCoveragePage)
                .ToArray();
    }

    private IEnumerable<SettingsNavigationItemInfo> CreateAccountSearchItems(MailAccount account)
    {
        var accountTitle = !string.IsNullOrWhiteSpace(account.Name)
            ? account.Name
            : !string.IsNullOrWhiteSpace(account.Address) ? account.Address : Translator.AccountDetailsPage_Title;
        var accountSearchText = string.Join(' ', new[] { account.Name, account.Address }.Where(value => !string.IsNullOrWhiteSpace(value)));

        yield return CreateAccountSearchItem(
            account,
            accountTitle,
            Translator.SettingsAccountDetails_Subtitle,
            WinoPage.AccountDetailsPage,
            AccountDetailsTab.General,
            accountSearchText,
            includeDestination: false);

        // People and To Do kept on this device have settings too, so the app being on is what counts.
        if (account.IsContactAccessEnabled)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.AccountDetailsPage_PeopleSourceTitle,
                Translator.AccountDetailsPage_PeopleSourceDescription,
                WinoPage.AccountDetailsPage,
                AccountDetailsTab.People,
                "contacts people address book source",
                includeDestination: false);
        }

        if (account.IsTaskAccessEnabled)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.AccountDetailsPage_TasksSourceTitle,
                Translator.AccountDetailsPage_TasksSourceDescription,
                WinoPage.AccountDetailsPage,
                AccountDetailsTab.ToDo,
                "tasks to do list source",
                includeDestination: false);
        }

        if (!account.IsMailAccessGranted)
            yield break;

        if (_entitlementService.CurrentEntitlement.CanAccessSurfaces)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.SemanticIndex_Title,
                Translator.SemanticIndex_Description,
                WinoPage.WinoIntelligenceManagementPage,
                AccountDetailsTab.Mail,
                "AI intelligence semantic search summarize");
        }
        yield return CreateAccountSearchItem(
            account,
            Translator.MailFilters_Title,
            Translator.MailFilters_Description,
            WinoPage.MailFiltersPage,
            AccountDetailsTab.Mail,
            "rules filters automation");
        yield return CreateAccountSearchItem(
            account,
            Translator.SettingsSignature_Title,
            Translator.SettingsSignature_Description,
            WinoPage.SignatureManagementPage,
            AccountDetailsTab.Mail,
            "signature");
        yield return CreateAccountSearchItem(
            account,
            Translator.FolderCustomization_Title,
            Translator.FolderCustomization_Description,
            WinoPage.FolderCustomizationPage,
            AccountDetailsTab.Mail,
            "folders folder list");

        var traits = _providers.GetTraits(account);

        if (traits.SupportsAliasSync)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.SettingsManageAliases_Title,
                Translator.SettingsManageAliases_Description,
                WinoPage.AliasManagementPage,
                AccountDetailsTab.Mail,
                "aliases sender addresses");
        }

        if (traits.SupportsCategorySync)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.MailCategoryManagementPage_Title,
                Translator.MailCategoryManagementPage_Description,
                WinoPage.MailCategoryManagementPage,
                AccountDetailsTab.Mail,
                "categories labels");
        }

        if (account.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.AccountSettingsDialog_AccountName,
                Translator.AccountMail_SenderNameDescription,
                WinoPage.AccountMailSettingsPage,
                AccountDetailsTab.Mail,
                "sender name display name from",
                includeDestination: false);

            yield return CreateAccountSearchItem(
                account,
                Translator.ImapCalDavSettingsPage_TitleEdit,
                Translator.ImapCalDavSettingsPage_Subtitle,
                WinoPage.ImapCalDavSettingsPage,
                AccountDetailsTab.General,
                "incoming SMTP mail server connection");
        }
    }

    private static SettingsNavigationItemInfo CreateAccountSearchItem(
        MailAccount account,
        string title,
        string description,
        WinoPage destinationPage,
        AccountDetailsTab accountTab,
        string searchKeywords,
        bool includeDestination = true)
    {
        var accountTitle = !string.IsNullOrWhiteSpace(account.Name)
            ? account.Name
            : !string.IsNullOrWhiteSpace(account.Address) ? account.Address : Translator.AccountDetailsPage_Title;
        var routeSteps = new List<SettingsNavigationRouteStep>
        {
            new(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage),
            new(accountTitle, WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(account.Id, AccountDetailsTab.General))
        };

        // Settings of one app live on that app's page under the account, so the route passes through it.
        WinoApplicationMode? app = accountTab switch
        {
            AccountDetailsTab.Mail => WinoApplicationMode.Mail,
            AccountDetailsTab.Calendar => WinoApplicationMode.Calendar,
            AccountDetailsTab.People => WinoApplicationMode.Contacts,
            AccountDetailsTab.ToDo => WinoApplicationMode.Tasks,
            _ => null
        };

        if (app is { } appMode)
        {
            var appPage = AccountAppPresenter.GetPage(appMode);
            routeSteps.Add(new SettingsNavigationRouteStep(AccountAppPresenter.GetTitle(appMode), appPage, account.Id));

            if (!includeDestination)
                destinationPage = appPage;
        }

        if (includeDestination)
            routeSteps.Add(new SettingsNavigationRouteStep(title, destinationPage, account.Id));

        var accountName = !string.IsNullOrWhiteSpace(account.Name) ? account.Name : account.Address;
        var contextualDescription = string.Format(Translator.SettingsAccountSubpage_Subtitle, title, accountName);

        return new SettingsNavigationItemInfo(
            destinationPage,
            title,
            contextualDescription,
            searchKeywords: $"{searchKeywords} {description} {account.Name} {account.Address}",
            navigationRoute: new SettingsNavigationRoute(routeSteps));
    }

    private async Task RefreshAccountSummaryAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var count = accounts?.Count ?? 0;
        var accountSearchItems = (accounts ?? []).SelectMany(CreateAccountSearchItems).ToArray();

        await ExecuteUIThread(() =>
        {
            ManageAccountsDescription = string.Format(Translator.SettingsOptions_AccountsSummary, count);
            _accountSearchItems = accountSearchItems;
            _isAccountSearchIndexInitialized = true;
        });
    }

    private async Task<string> GetPageDescriptionAsync(WinoPage pageType, object parameter, string pageTitle)
    {
        switch (pageType)
        {
            case WinoPage.AccountDetailsPage:
                var accountName = await GetAccountNameAsync(parameter).ConfigureAwait(false);
                return string.Format(Translator.SettingsAccountDetails_Subtitle, accountName ?? pageTitle ?? Translator.AccountDetailsPage_Title);
            case WinoPage.MergedAccountDetailsPage:
                return parameter is AccountGroupDetailsNavigationContext { GroupId: null }
                    ? Translator.AccountGroupDetailsPage_NewDescription
                    : Translator.SettingsEditLinkedInbox_Description;
            case WinoPage.AliasManagementPage:
                return Translator.SettingsManageAliases_Description;
            case WinoPage.FolderCustomizationPage:
                return Translator.FolderCustomization_Description;
            case WinoPage.MailCategoryManagementPage:
                return Translator.MailCategoryManagementPage_Description;
            case WinoPage.MailFiltersPage:
            case WinoPage.MailFilterEditorPage:
                return Translator.MailFilters_Description;
            case WinoPage.SignatureManagementPage:
                return Translator.SettingsSignature_Description;
            case WinoPage.ImapCalDavSettingsPage:
                return Translator.ImapCalDavSettingsPage_Subtitle;
            case WinoPage.AccountTroubleshootingPage:
                return string.Format(Translator.AccountTroubleshooting_PageDescription, await GetAccountNameAsync(parameter).ConfigureAwait(false) ?? pageTitle);
            case WinoPage.AccountSignInPage:
                return string.Format(Translator.AccountSignIn_PageDescription, await GetAccountNameAsync(parameter).ConfigureAwait(false) ?? pageTitle);
            case WinoPage.WinoIntelligenceManagementPage:
                return Translator.SemanticIndex_PageDescription;
            case WinoPage.IntelligenceCoveragePage:
                return Translator.SemanticIndex_CoverageEditorPageDescription;
            case WinoPage.WinoIntelligencePage:
                return Translator.WinoIntelligence_SettingsDescription;
            case WinoPage.VoiceControlSettingsPage:
                return Translator.VoiceControlSettings_Description;
            case WinoPage.VoiceBrainPage:
                return Translator.VoiceBrain_Description;
            case WinoPage.MailClassificationPage:
                return Translator.WinoIntelligence_MailClassification_PageDescription;
            case WinoPage.ProviderSelectionPage:
            case WinoPage.AccountSetupPage:
            case WinoPage.ExchangeSettingsPage:
            case WinoPage.SpecialImapCredentialsPage:
            case WinoPage.AccountSetupProgressPage:
                return Translator.SettingsHome_ManageAccounts_Description;
            case WinoPage.CreateEmailTemplatePage:
                return Translator.SettingsEmailTemplates_NewTemplateDescription;
            case WinoPage.ReaderActionsPage:
                return Translator.ReaderActions_Description;
            case WinoPage.CalendarAccountSettingsPage:
                var calendarAccountName = await GetCalendarAccountNameAsync(parameter).ConfigureAwait(false);
                return string.Format(
                    Translator.CalendarAccountSettings_Description,
                    calendarAccountName ?? pageTitle ?? Translator.CalendarSettings_Preferences_Title);
        }

        var rootPage = SettingsNavigationInfoProvider.GetRootPage(pageType);

        if (rootPage == WinoPage.ManageAccountsPage && parameter is Guid)
        {
            var accountName = await GetAccountNameAsync(parameter).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(accountName))
            {
                return string.Format(
                    Translator.SettingsAccountSubpage_Subtitle,
                    pageTitle ?? SettingsNavigationInfoProvider.GetPageTitle(pageType),
                    accountName);
            }
        }

        return SettingsNavigationInfoProvider.GetInfo(rootPage, ManageAccountsDescription).Description;
    }

    private async Task<string> GetAccountNameAsync(object parameter)
    {
        var accountId = parameter switch
        {
            Guid id => id,
            AccountDetailsNavigationContext context => context.AccountId,
            _ => Guid.Empty
        };

        if (accountId == Guid.Empty)
            return null;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        return account?.Name;
    }

    private Task<string> GetCalendarAccountNameAsync(object parameter)
        => GetAccountNameAsync(parameter is AccountCalendar calendar ? calendar.AccountId : parameter);
}
