using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// Selected item removed event.
/// </summary>
public class MailItemSelectionRemovedEvent
{
    public MailItemSelectionRemovedEvent(MailItemViewModel removedMailItem)
    {
        RemovedMailItem = removedMailItem;
    }

    public MailItemViewModel RemovedMailItem { get; set; }
}
