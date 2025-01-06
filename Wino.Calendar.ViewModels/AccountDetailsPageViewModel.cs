using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Calendar.ViewModels
{
    public partial class AccountDetailsPageViewModel : CalendarBaseViewModel
    {
        private readonly IAccountService _accountService;

        public AccountProviderDetailViewModel Account { get; private set; }
        public ICalendarDialogService CalendarDialogService { get; }
        public IAccountCalendarStateService AccountCalendarStateService { get; }

        public AccountDetailsPageViewModel(ICalendarDialogService calendarDialogService, IAccountService accountService, IAccountCalendarStateService accountCalendarStateService)
        {
            CalendarDialogService = calendarDialogService;
            _accountService = accountService;
            AccountCalendarStateService = accountCalendarStateService;
        }

        [RelayCommand]
        private async Task RenameAccount()
        {
            if (Account == null)
                return;

            var updatedAccount = await CalendarDialogService.ShowEditAccountDialogAsync(Account.Account);

            if (updatedAccount != null)
            {
                await _accountService.UpdateAccountAsync(updatedAccount);

                ReportUIChange(new AccountUpdatedMessage(updatedAccount));
            }
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);
        }
    }
}
