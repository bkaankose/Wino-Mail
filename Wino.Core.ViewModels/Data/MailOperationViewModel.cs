using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// ViewModel for displaying mail operations in dropdowns/lists.
/// </summary>
public class MailOperationViewModel
{
    public MailOperation Operation { get; }

    public string DisplayName
    {
        get
        {
            return Operation switch
            {
                MailOperation.Archive => "Archive",
                MailOperation.UnArchive => "Unarchive",
                MailOperation.SoftDelete => "Delete",
                MailOperation.Move => "Move",
                MailOperation.MoveToJunk => "Move to Junk",
                MailOperation.SetFlag => "Set Flag",
                MailOperation.ClearFlag => "Clear Flag",
                MailOperation.MarkAsRead => "Mark as Read",
                MailOperation.MarkAsUnread => "Mark as Unread",
                MailOperation.Reply => "Reply",
                MailOperation.ReplyAll => "Reply All",
                MailOperation.Forward => "Forward",
                _ => Operation.ToString()
            };
        }
    }

    public MailOperationViewModel(MailOperation operation)
    {
        Operation = operation;
    }

    public override string ToString() => DisplayName;
}
