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

    [ObservableProperty]
    private MailAccount _account;

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string SenderName { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedColor { get; set; }


    [ObservableProperty]
    public partial List<AppColorViewModel> AvailableColors { get; set; }

    public EditAccountDetailsPageViewModel(IAccountService accountService)
    {
        _accountService = accountService;

        AvailableColors = new List<AppColorViewModel>
        {
            // Reds
            new AppColorViewModel("#e74c3c"),
            new AppColorViewModel("#c0392b"),
            new AppColorViewModel("#e53935"),
            new AppColorViewModel("#d81b60"),
            
            // Pinks
            new AppColorViewModel("#e91e63"),
            new AppColorViewModel("#ec407a"),
            new AppColorViewModel("#ff4081"),

            // Purples
            new AppColorViewModel("#9b59b6"),
            new AppColorViewModel("#8e44ad"),
            new AppColorViewModel("#673ab7"),

            // Blues
            new AppColorViewModel("#3498db"),
            new AppColorViewModel("#2980b9"),
            new AppColorViewModel("#2196f3"),
            new AppColorViewModel("#03a9f4"),
            new AppColorViewModel("#00bcd4"),

            // Teals
            new AppColorViewModel("#009688"),
            new AppColorViewModel("#1abc9c"),
            new AppColorViewModel("#16a085"),

            // Greens
            new AppColorViewModel("#2ecc71"),
            new AppColorViewModel("#27ae60"),
            new AppColorViewModel("#4caf50"),
            new AppColorViewModel("#8bc34a"),

            // Yellows & Oranges
            new AppColorViewModel("#f1c40f"),
            new AppColorViewModel("#f39c12"),
            new AppColorViewModel("#ff9800"),
            new AppColorViewModel("#ff5722"),

            // Browns
            new AppColorViewModel("#795548"),
            new AppColorViewModel("#a0522d"),

            // Grays
            new AppColorViewModel("#9e9e9e"),
            new AppColorViewModel("#607d8b"),
            new AppColorViewModel("#34495e"),
            new AppColorViewModel("#2c3e50"),
        };
    }

    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        Account.Name = AccountName;
        Account.SenderName = SenderName;
        Account.AccountColorHex = SelectedColor == null ? string.Empty : SelectedColor.Hex;

        await _accountService.UpdateAccountAsync(Account);

        Messenger.Send(new BackBreadcrumNavigationRequested());
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
