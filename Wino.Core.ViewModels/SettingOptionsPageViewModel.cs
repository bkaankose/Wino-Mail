using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingOptionsPageViewModel : CoreBaseViewModel
{
    private readonly INativeAppService _nativeAppService;
    private readonly IAccountService _accountService;
    private readonly IStoreRatingService _storeRatingService;

    public string GitHubUrl => AppUrls.GitHub;
    public string PaypalUrl => AppUrls.Paypal;

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AccountCount { get; set; }

    public SettingOptionsPageViewModel(INativeAppService nativeAppService,
                                       IAccountService accountService,
                                       IStoreRatingService storeRatingService)
    {
        _nativeAppService = nativeAppService;
        _accountService = accountService;
        _storeRatingService = storeRatingService;
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
