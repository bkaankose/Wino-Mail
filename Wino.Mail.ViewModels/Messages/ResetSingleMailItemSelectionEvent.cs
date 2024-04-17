using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages
{

    /// <summary>
    /// When a thread conversation listview has single selection, all other listviews
    /// must unselect all their items.
    /// </summary>
    public class ResetSingleMailItemSelectionEvent
    {
        public ResetSingleMailItemSelectionEvent(MailItemViewModel selectedViewModel)
        {
            SelectedViewModel = selectedViewModel;
        }

        public MailItemViewModel SelectedViewModel { get; set; }
    }
}
