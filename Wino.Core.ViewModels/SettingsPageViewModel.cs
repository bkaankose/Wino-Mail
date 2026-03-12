using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
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

    public async Task UpdateActivePageAsync(WinoPage pageType)
    {
        await EnsureAccountSummaryAsync();

        var info = SettingsNavigationInfoProvider.GetInfo(pageType, ManageAccountsDescription);
        await ExecuteUIThread(() => CurrentDescription = info.Description);
    }

    private async Task EnsureAccountSummaryAsync()
    {
        if (!string.IsNullOrWhiteSpace(ManageAccountsDescription))
            return;

        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var count = accounts?.Count ?? 0;

        await ExecuteUIThread(() =>
        {
            ManageAccountsDescription = string.Format(Translator.SettingsOptions_AccountsSummary, count);
        });
    }
}
