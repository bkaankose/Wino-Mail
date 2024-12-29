using System.Collections.ObjectModel;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.ViewModels.Interfaces
{
    public interface IAccountCalendarStateService
    {
        ObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }
    }
}
