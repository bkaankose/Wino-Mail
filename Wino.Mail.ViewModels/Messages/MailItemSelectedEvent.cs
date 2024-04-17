using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages
{
    /// <summary>
    /// Wino has complex selected item detection mechanism with nested ListViews that
    /// supports multi selection with threads. Each list view will raise this for mail list page
    /// to react.
    /// </summary>
    public class MailItemSelectedEvent
    {
        public MailItemSelectedEvent(MailItemViewModel selectedMailItem)
        {
            SelectedMailItem = selectedMailItem;
        }

        public MailItemViewModel SelectedMailItem { get; set; }
    }
}
