using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Core.ViewModels;

public partial class SettingsPageViewModel : CoreBaseViewModel
{
    private readonly IAccountService _accountService;

    public SettingsPageViewModel(
        INavigationService navigationService,
        IStatePersistanceService statePersistenceService,
        IAccountService accountService)
    {
        NavigationService = navigationService;
        StatePersistenceService = statePersistenceService;
        _accountService = accountService;
    }

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

    private async Task RefreshAccountSummaryAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var count = accounts?.Count ?? 0;

        await ExecuteUIThread(() =>
        {
            ManageAccountsDescription = string.Format(Translator.SettingsOptions_AccountsSummary, count);
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
            case WinoPage.WinoAccountConsentPage:
                return Translator.WinoAccount_ConsentNavigationDescription;
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
        if (parameter is not Guid accountId)
            return null;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        return account?.Name;
    }

    private Task<string> GetCalendarAccountNameAsync(object parameter)
        => GetAccountNameAsync(parameter is AccountCalendar calendar ? calendar.AccountId : parameter);
}
