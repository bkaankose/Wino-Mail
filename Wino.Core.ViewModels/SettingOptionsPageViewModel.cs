using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Extensions;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingOptionsPageViewModel : CoreBaseViewModel
{
    private readonly INativeAppService _nativeAppService;
    private readonly IAccountService _accountService;
    private readonly IMimeStorageService _mimeStorageService;
    private readonly IStoreRatingService _storeRatingService;

    public string GitHubUrl => AppUrls.GitHub;
    public string PaypalUrl => AppUrls.Paypal;
    public string WebsiteUrl => AppUrls.Website;
    public string PrivacyPolicyUrl => AppUrls.PrivacyPolicy;

    public ObservableCollection<SettingsNavigationItemInfo> SearchSuggestions { get; } = [];

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

    public SettingOptionsPageViewModel(INativeAppService nativeAppService,
                                       IAccountService accountService,
                                       IMimeStorageService mimeStorageService,
                                       IStoreRatingService storeRatingService)
    {
        _nativeAppService = nativeAppService;
        _accountService = accountService;
        _mimeStorageService = mimeStorageService;
        _storeRatingService = storeRatingService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        VersionText = string.Format("{0}{1}", Translator.SettingsAboutVersion, _nativeAppService.GetFullAppVersion());
        SearchQuery = string.Empty;
        SearchSuggestions.Clear();
        StorageSummaryText = Translator.SettingsHome_StorageLoading;

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

    private async Task LoadDashboardAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var count = accounts.Count;
        Dictionary<Guid, long> storageSizeMap = count == 0
            ? []
            : await _mimeStorageService.GetAccountsMimeStorageSizesAsync(accounts.Select(account => account.Id)).ConfigureAwait(false);
        var totalStorageBytes = storageSizeMap.Values.Sum();

        await ExecuteUIThread(() =>
        {
            AccountCount = count;
            AccountSummaryText = string.Format(Translator.SettingsOptions_AccountsSummary, count);
            StorageSummaryText = totalStorageBytes == 0
                ? Translator.SettingsHome_StorageEmptySummary
                : string.Format(Translator.SettingsStorage_TotalUsage, totalStorageBytes.GetBytesReadable());

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                UpdateSearchSuggestions(SearchQuery);
            }
        });
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
