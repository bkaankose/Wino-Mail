using System.Collections.Generic;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Class that holds information when the drag/drop of mails are performed.
/// </summary>
public class MailDragPackage
{
    public MailDragPackage(IEnumerable<IMailItem> draggingMails)
    {
        DraggingMails = draggingMails;
    }

    public MailDragPackage(IMailItem draggingMail)
    {
        DraggingMails =
        [
            draggingMail
        ];
    }

    public IEnumerable<IMailItem> DraggingMails { get; set; }
}
