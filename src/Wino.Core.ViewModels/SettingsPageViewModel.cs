using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingsPageViewModel : CoreBaseViewModel, IShellMenuOwner
{
    private readonly IAccountService _accountService;
    private IReadOnlyList<SettingsNavigationItemInfo> _accountSearchItems = [];
    private bool _isAccountSearchIndexInitialized;

    public SettingsPageViewModel(
        INavigationService navigationService,
        IStatePersistanceService statePersistenceService,
        IAccountService accountService,
        SettingsMenuProvider settingsMenuProvider)
    {
        NavigationService = navigationService;
        StatePersistenceService = statePersistenceService;
        _accountService = accountService;
        ShellMenuProvider = settingsMenuProvider;
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
        await RefreshAccountSummaryAsync();

        var description = await GetPageDescriptionAsync(pageType, parameter, pageTitle).ConfigureAwait(false);
        await ExecuteUIThread(() => CurrentDescription = description);
    }

    public async Task<IReadOnlyList<SettingsNavigationItemInfo>> SearchSettingsAsync(string query)
    {
        if (!_isAccountSearchIndexInitialized)
            await RefreshAccountSummaryAsync().ConfigureAwait(false);

        return SettingsNavigationInfoProvider.Search(query, ManageAccountsDescription, _accountSearchItems);
    }

    private static IEnumerable<SettingsNavigationItemInfo> CreateAccountSearchItems(MailAccount account)
    {
        var accountTitle = !string.IsNullOrWhiteSpace(account.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account.Name ?? Translator.AccountDetailsPage_Title;
        var accountSearchText = string.Join(' ', new[] { account.Name, account.Address }.Where(value => !string.IsNullOrWhiteSpace(value)));

        yield return CreateAccountSearchItem(
            account,
            accountTitle,
            Translator.SettingsAccountDetails_Subtitle,
            WinoPage.AccountDetailsPage,
            AccountDetailsTab.General,
            accountSearchText,
            includeDestination: false);

        if (account.IsContactAccessGranted)
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

        if (account.IsTaskAccessGranted)
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

        yield return CreateAccountSearchItem(
            account,
            Translator.SemanticIndex_Title,
            Translator.SemanticIndex_Description,
            WinoPage.WinoIntelligenceManagementPage,
            AccountDetailsTab.Mail,
            "AI intelligence semantic search summarize");
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

        if (account.IsAliasSyncSupported)
        {
            yield return CreateAccountSearchItem(
                account,
                Translator.SettingsManageAliases_Title,
                Translator.SettingsManageAliases_Description,
                WinoPage.AliasManagementPage,
                AccountDetailsTab.Mail,
                "aliases sender addresses");
        }

        if (account.IsCategorySyncSupported)
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
        var accountTitle = !string.IsNullOrWhiteSpace(account.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account.Name ?? Translator.AccountDetailsPage_Title;
        var routeSteps = new List<SettingsNavigationRouteStep>
        {
            new(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage),
            new(accountTitle, WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(account.Id, accountTab))
        };

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
                return Translator.SettingsEditLinkedInbox_Description;
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
            case WinoPage.WinoIntelligenceManagementPage:
                return Translator.SemanticIndex_PageDescription;
            case WinoPage.IntelligenceCoveragePage:
                return Translator.SemanticIndex_CoverageEditorPageDescription;
            case WinoPage.WinoIntelligencePage:
                return Translator.WinoIntelligence_SettingsDescription;
            case WinoPage.ProviderSelectionPage:
            case WinoPage.SpecialImapCredentialsPage:
            case WinoPage.AccountSetupProgressPage:
                return Translator.SettingsHome_ManageAccounts_Description;
            case WinoPage.CreateEmailTemplatePage:
                return Translator.SettingsEmailTemplates_NewTemplateDescription;
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
