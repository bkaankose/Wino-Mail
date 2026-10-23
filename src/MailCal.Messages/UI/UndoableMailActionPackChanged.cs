using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Messaging.UI;

public enum UndoableMailActionPackState
{
    Queued,
    Undone,
    Expired
}

public record UndoableMailActionPackChanged(
    UndoableMailActionPack Pack,
    UndoableMailActionPackState State) : UIMessageBase<UndoableMailActionPackChanged>;
