using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Calendar.ViewModels;

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
    private void EditAccountDetails()
            => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsEditAccountDetails_Title, WinoPage.EditAccountDetailsPage, Account));

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
    }
}
