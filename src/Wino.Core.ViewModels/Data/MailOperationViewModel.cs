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
                MailOperation.Archive => Translator.MailOperation_Archive,
                MailOperation.UnArchive => Translator.MailOperation_Unarchive,
                MailOperation.SoftDelete => Translator.MailOperation_Delete,
                MailOperation.Move => Translator.MailOperation_Move,
                MailOperation.MoveToJunk => Translator.MailOperation_MoveJunk,
                MailOperation.SetFlag => Translator.MailOperation_SetFlag,
                MailOperation.ClearFlag => Translator.MailOperation_ClearFlag,
                MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
                MailOperation.MarkAsUnread => Translator.MailOperation_MarkAsUnread,
                MailOperation.Reply => Translator.MailOperation_Reply,
                MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
                MailOperation.Forward => Translator.MailOperation_Forward,
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
