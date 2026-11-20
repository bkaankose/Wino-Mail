using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Messaging.UI;

public enum UndoableMailActionPackState
{
    Queued,
    Undone,
    Expired
}

public record UndoableMailActionPackChanged(
    UndoableMailActionPack Pack,
    UndoableMailActionPackState State) : UIMessageBase<UndoableMailActionPackChanged>;
