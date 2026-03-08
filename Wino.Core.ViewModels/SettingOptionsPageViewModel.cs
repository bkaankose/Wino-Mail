using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingOptionsPageViewModel : CoreBaseViewModel
{
    private readonly INativeAppService _nativeAppService;
    private readonly IAccountService _accountService;

    public string WebsiteUrl => AppUrls.Website;
    public string PaypalUrl => AppUrls.Paypal;

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AccountCount { get; set; }

    public SettingOptionsPageViewModel(INativeAppService nativeAppService, IAccountService accountService)
    {
        _nativeAppService = nativeAppService;
        _accountService = accountService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        VersionText = string.Format("{0}{1}", Translator.SettingsAboutVersion, _nativeAppService.GetFullAppVersion());
        _ = LoadAccountSummaryAsync();
    }

    private async Task LoadAccountSummaryAsync()
    {
        var accounts = await _accountService.GetAccountsAsync();
        int count = accounts?.Count ?? 0;

        await ExecuteUIThread(() =>
        {
            AccountCount = count;
            AccountSummaryText = string.Format(Translator.SettingsOptions_AccountsSummary, count);
        });
    }

    [RelayCommand]
    private void GoAccountSettings() => Messenger.Send<NavigateManageAccountsRequested>();

    [RelayCommand]
    public void NavigateSubDetail(object type)
    {
        if (type is WinoPage pageType)
        {
            if (pageType == WinoPage.AccountManagementPage)
            {
                GoAccountSettings();
                return;
            }

            string pageTitle = pageType switch
            {
                WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
                WinoPage.AboutPage => Translator.SettingsAbout_Title,
                WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
                WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
                WinoPage.LanguageTimePage => Translator.SettingsLanguageTime_Title,
                WinoPage.AppPreferencesPage => Translator.SettingsAppPreferences_Title,
                WinoPage.EmailTemplatesPage => Translator.SettingsEmailTemplates_Title,
                WinoPage.CalendarSettingsPage => Translator.SettingsCalendarSettings_Title,
                WinoPage.SignatureAndEncryptionPage => Translator.SettingsSignatureAndEncryption_Title,
                WinoPage.KeyboardShortcutsPage => Translator.Settings_KeyboardShortcuts_Title,
                WinoPage.StoragePage => Translator.SettingsStorage_Title,
                _ => throw new NotImplementedException()
            };

            Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
        }
    }
}
