using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;

namespace Wino.Calendar.Services
{
    public partial class AccountCalendarStateService : ObservableObject, IAccountCalendarStateService
    {
        [ObservableProperty]
        private ObservableCollection<GroupedAccountCalendarViewModel> _groupedAccountCalendars = new ObservableCollection<GroupedAccountCalendarViewModel>();
    }
}
