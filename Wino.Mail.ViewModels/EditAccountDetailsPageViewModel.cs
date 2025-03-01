using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class EditAccountDetailsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IThemeService _themeService;

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string SenderName { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedColor { get; set; }


    [ObservableProperty]
    public partial List<AppColorViewModel> AvailableColors { get; set; }

    public EditAccountDetailsPageViewModel(IAccountService accountService, IThemeService themeService)
    {
        _accountService = accountService;
        _themeService = themeService;

        var colorHexList = _themeService.GetAvailableAccountColors();

        AvailableColors = colorHexList.Select(a => new AppColorViewModel(a)).ToList();
    }

    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        await UpdateAccountAsync();

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    private Task UpdateAccountAsync()
    {
        Account.Name = AccountName;
        Account.SenderName = SenderName;
        Account.AccountColorHex = SelectedColor == null ? string.Empty : SelectedColor.Hex;

        return _accountService.UpdateAccountAsync(Account);
    }

    [RelayCommand]
    private void ResetColor()
        => SelectedColor = null;

    partial void OnSelectedColorChanged(AppColorViewModel oldValue, AppColorViewModel newValue)
    {
        _ = UpdateAccountAsync();
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is MailAccount account)
        {
            Account = account;
            AccountName = account.Name;
            SenderName = account.SenderName;

            if (!string.IsNullOrEmpty(account.AccountColorHex))
            {
                SelectedColor = AvailableColors.FirstOrDefault(a => a.Hex == account.AccountColorHex);
            }
        }
    }
}
