using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Class that holds information when the drag/drop of mails are performed.
/// </summary>
public class MailDragPackage
{
    public MailDragPackage(IEnumerable<object> draggingMails)
    {
        DraggingMails = draggingMails;
    }

    public MailDragPackage(MailCopy draggingMail)
    {
        DraggingMails =
        [
            draggingMail
        ];
    }

    public IEnumerable<object> DraggingMails { get; set; }
}
