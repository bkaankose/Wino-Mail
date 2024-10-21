using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels
{
    public class AppShellViewModel : CoreBaseViewModel
    {
        private readonly ICalendarDialogService _dialogService;

        public AppShellViewModel(ICalendarDialogService dialogService)
        {
            _dialogService = dialogService;
        }
    }
}
