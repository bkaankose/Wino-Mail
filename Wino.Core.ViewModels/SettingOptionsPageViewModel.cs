using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Translations;
using Wino.Core.Extensions;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingOptionsPageViewModel : CoreBaseViewModel
{
    private readonly INativeAppService _nativeAppService;
    private readonly IAccountService _accountService;
    private readonly IMimeStorageService _mimeStorageService;
    private readonly IStoreRatingService _storeRatingService;
    private readonly ITranslationService _translationService;
    private readonly INewThemeService _newThemeService;
    private readonly IPreferencesService _preferencesService;
    private readonly IProviderService _providerService;
    private bool _isInitializingSettings;
    private bool _isAppearanceSelectionPaused;

    public string GitHubUrl => AppUrls.GitHub;
    public string PaypalUrl => AppUrls.Paypal;
    public string WebsiteUrl => AppUrls.Website;
    public string PrivacyPolicyUrl => AppUrls.PrivacyPolicy;

    public ObservableCollection<SettingsNavigationItemInfo> SearchSuggestions { get; } = [];
    public ObservableCollection<IAccountProviderDetailViewModel> Accounts { get; } = [];
    public ObservableCollection<AppColorViewModel> Colors { get; } = [];

    public List<ElementThemeContainer> ElementThemes { get; } =
    [
        new(ApplicationElementTheme.Default, Translator.ElementTheme_Default),
        new(ApplicationElementTheme.Light, Translator.ElementTheme_Light),
        new(ApplicationElementTheme.Dark, Translator.ElementTheme_Dark),
    ];

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AccountCount { get; set; }

    [ObservableProperty]
    public partial string StorageSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial List<AppLanguageModel> AvailableLanguages { get; set; } = [];

    [ObservableProperty]
    public partial AppLanguageModel SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ElementThemeContainer SelectedElementTheme { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedAppColor { get; set; }

    [ObservableProperty]
    public partial bool UseAccentColor { get; set; }

    public SettingOptionsPageViewModel(INativeAppService nativeAppService,
                                        IAccountService accountService,
                                        IMimeStorageService mimeStorageService,
                                         IStoreRatingService storeRatingService,
                                          ITranslationService translationService,
                                          INewThemeService newThemeService,
                                          IPreferencesService preferencesService,
                                         IProviderService providerService)
    {
        _nativeAppService = nativeAppService;
        _accountService = accountService;
        _mimeStorageService = mimeStorageService;
        _storeRatingService = storeRatingService;
        _translationService = translationService;
        _newThemeService = newThemeService;
        _preferencesService = preferencesService;
        _providerService = providerService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        VersionText = string.Format("{0}{1}", Translator.SettingsAboutVersion, _nativeAppService.GetFullAppVersion());
        SearchQuery = string.Empty;
        SearchSuggestions.Clear();
        StorageSummaryText = Translator.SettingsHome_StorageLoading;
        InitializeQuickSettings();

        _ = LoadDashboardAsync();
    }

    public void UpdateSearchSuggestions(string query)
    {
        SearchQuery = query;

        SearchSuggestions.Clear();

        foreach (var result in SettingsNavigationInfoProvider.Search(query, AccountSummaryText).Take(6))
        {
            SearchSuggestions.Add(result);
        }
    }

    public SettingsNavigationItemInfo GetBestSearchSuggestion(string query)
        => SettingsNavigationInfoProvider.Search(query, AccountSummaryText).FirstOrDefault();

    public void NavigateToSetting(SettingsNavigationItemInfo item)
    {
        if (item?.PageType is WinoPage pageType)
        {
            NavigateSubDetail(pageType);
        }
    }

    public void NavigateToAccount(IAccountProviderDetailViewModel account)
    {
        if (account == null)
        {
            return;
        }

        Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage));

        switch (account)
        {
            case AccountProviderDetailViewModel accountDetails:
                Messenger.Send(new BreadcrumbNavigationRequested(GetAccountDetailsTitle(accountDetails.Account), WinoPage.AccountDetailsPage, accountDetails.Account.Id));
                break;
            case MergedAccountProviderDetailViewModel mergedAccount:
                Messenger.Send(new BreadcrumbNavigationRequested(mergedAccount.MergedInbox.Name, WinoPage.MergedAccountDetailsPage, mergedAccount));
                break;
        }
    }

    public void NavigateToAddAccount()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage));
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.WelcomeWizard_Step2Title,
            WinoPage.ProviderSelectionPage,
            ProviderSelectionNavigationContext.CreateForSettingsAddAccount()));
    }

    public void NavigateToManageAccounts()
    {
        Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsManageAccountSettings_Title, WinoPage.ManageAccountsPage));
    }

    private async Task LoadDashboardAsync()
    {
        var accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? []).ToList();
        var count = accounts.Count;
        Dictionary<Guid, long> storageSizeMap = count == 0
            ? []
            : await _mimeStorageService.GetAccountsMimeStorageSizesAsync(accounts.Select(account => account.Id)).ConfigureAwait(false);
        var totalStorageBytes = storageSizeMap.Values.Sum();
        var groupedAccountItems = CreateAccountItems(accounts);

        await ExecuteUIThread(() =>
        {
            AccountCount = count;
            AccountSummaryText = string.Format(Translator.SettingsOptions_AccountsSummary, count);
            Accounts.Clear();

            foreach (var account in groupedAccountItems)
            {
                Accounts.Add(account);
            }

            OnPropertyChanged(nameof(HasAccounts));
            StorageSummaryText = totalStorageBytes == 0
                ? Translator.SettingsHome_StorageEmptySummary
                : string.Format(Translator.SettingsStorage_TotalUsage, totalStorageBytes.GetBytesReadable());

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                UpdateSearchSuggestions(SearchQuery);
            }
        });
    }

    private static string GetAccountDetailsTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account?.Address)
            ? string.Format(Translator.SettingsAccountDetails_NavigationTitle, account.Address)
            : account?.Name ?? Translator.AccountDetailsPage_Title;

    private void InitializeQuickSettings()
    {
        _isInitializingSettings = true;
        InitializeColors();
        InitializeLanguageOptions();
        InitializeAppearanceOptions();
        _isInitializingSettings = false;
    }

    private void InitializeLanguageOptions()
    {
        AvailableLanguages = _translationService.GetAvailableLanguages();
        SelectedLanguage = AvailableLanguages.FirstOrDefault(language => language.Language == _preferencesService.CurrentLanguage)
                           ?? AvailableLanguages.FirstOrDefault();
    }

    private void InitializeColors()
    {
        Colors.Clear();

        foreach (var color in _newThemeService.GetAvailableAccountColors().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Colors.Add(new AppColorViewModel(color));
        }

        var systemAccentColor = _newThemeService.GetSystemAccentColorHex();

        if (Colors.All(color => !string.Equals(color.Hex, systemAccentColor, StringComparison.OrdinalIgnoreCase)))
        {
            Colors.Add(new AppColorViewModel(systemAccentColor, true));
        }
        else
        {
            var matchingAccentColor = Colors.First(color => string.Equals(color.Hex, systemAccentColor, StringComparison.OrdinalIgnoreCase));
            Colors.Remove(matchingAccentColor);
            Colors.Add(new AppColorViewModel(systemAccentColor, true));
        }
    }

    private void InitializeAppearanceOptions()
    {
        _isAppearanceSelectionPaused = true;

        var currentAccentColor = _newThemeService.AccentColor;

        if (!string.IsNullOrWhiteSpace(currentAccentColor) &&
            Colors.All(color => !string.Equals(color.Hex, currentAccentColor, StringComparison.OrdinalIgnoreCase)))
        {
            Colors.Insert(0, new AppColorViewModel(currentAccentColor));
        }

        SelectedElementTheme = ElementThemes.FirstOrDefault(theme => theme.NativeTheme == _newThemeService.RootTheme)
                               ?? ElementThemes.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(currentAccentColor))
        {
            SelectedAppColor = Colors.LastOrDefault(color => color.IsAccentColor) ?? Colors.LastOrDefault();
            UseAccentColor = true;
        }
        else
        {
            SelectedAppColor = Colors.FirstOrDefault(color => string.Equals(color.Hex, currentAccentColor, StringComparison.OrdinalIgnoreCase))
                               ?? Colors.FirstOrDefault();
            UseAccentColor = SelectedAppColor?.IsAccentColor == true;
        }

        _isAppearanceSelectionPaused = false;
    }

    private List<IAccountProviderDetailViewModel> CreateAccountItems(List<MailAccount> accounts)
    {
        var groupedAccounts = accounts
            .OrderBy(account => account.MergedInboxId == null ? 1 : 0)
            .ThenBy(account => account.Order)
            .ThenBy(account => account.Name)
            .GroupBy(account => account.MergedInboxId);
        var accountItems = new List<IAccountProviderDetailViewModel>();

        foreach (var accountGroup in groupedAccounts)
        {
            if (accountGroup.Key == null)
            {
                accountItems.AddRange(accountGroup.Select(CreateAccountProviderDetails));
                continue;
            }

            var mergedInbox = accountGroup.First().MergedInbox;
            var holdingAccounts = accountGroup
                .Select(CreateAccountProviderDetails)
                .ToList();
            var mergedAccount = new MergedAccountProviderDetailViewModel(mergedInbox, holdingAccounts)
            {
                ProviderDetail = holdingAccounts.FirstOrDefault()?.ProviderDetail
            };

            accountItems.Add(mergedAccount);
        }

        return accountItems;
    }

    private AccountProviderDetailViewModel CreateAccountProviderDetails(MailAccount account)
    {
        var provider = _providerService.GetProviderDetail(account.ProviderType);
        return new AccountProviderDetailViewModel(provider, account);
    }

    partial void OnSelectedLanguageChanged(AppLanguageModel value)
    {
        if (_isInitializingSettings || value == null)
        {
            return;
        }

        _ = ApplyLanguageAsync(value);
    }

    partial void OnSelectedElementThemeChanged(ElementThemeContainer value)
    {
        if (_isInitializingSettings || value == null)
        {
            return;
        }

        _newThemeService.RootTheme = value.NativeTheme;
    }

    partial void OnSelectedAppColorChanged(AppColorViewModel value)
    {
        if (_isInitializingSettings || _isAppearanceSelectionPaused || value == null)
        {
            return;
        }

        _isAppearanceSelectionPaused = true;
        UseAccentColor = value.IsAccentColor;
        _isAppearanceSelectionPaused = false;

        _newThemeService.AccentColor = value.Hex;
    }

    partial void OnUseAccentColorChanged(bool value)
    {
        if (_isInitializingSettings || _isAppearanceSelectionPaused || Colors.Count == 0)
        {
            return;
        }

        var accentColor = Colors.LastOrDefault(color => color.IsAccentColor);
        var fallbackColor = Colors.FirstOrDefault(color => !color.IsAccentColor) ?? Colors.FirstOrDefault();
        var targetColor = value ? accentColor : SelectedAppColor?.IsAccentColor == true ? fallbackColor : SelectedAppColor;

        if (targetColor == null || ReferenceEquals(targetColor, SelectedAppColor))
        {
            return;
        }

        _isAppearanceSelectionPaused = true;
        SelectedAppColor = targetColor;
        _isAppearanceSelectionPaused = false;

        _newThemeService.AccentColor = targetColor.Hex;
    }

    private async Task ApplyLanguageAsync(AppLanguageModel language)
    {
        await _translationService.InitializeLanguageAsync(language.Language);
    }

    [RelayCommand]
    public void NavigateSubDetail(object type)
    {
        if (type is WinoPage pageType)
        {
            var pageInfo = SettingsNavigationInfoProvider.GetInfo(pageType, AccountSummaryText);
            Messenger.Send(new BreadcrumbNavigationRequested(pageInfo.Title, pageType));
        }
    }

    [RelayCommand]
    private async Task NavigateExternalAsync(object target)
    {
        if (target is not string stringTarget || string.IsNullOrWhiteSpace(stringTarget))
            return;

        if (stringTarget == "Store")
        {
            await _storeRatingService.LaunchStorePageForReviewAsync();
            return;
        }

        await _nativeAppService.LaunchUriAsync(new Uri(stringTarget));
    }
}
