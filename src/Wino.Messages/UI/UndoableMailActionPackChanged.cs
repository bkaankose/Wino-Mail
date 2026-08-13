using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.UI;

public enum UndoableMailActionPackState
{
    Queued,
    Undone,
    Expired
}

public record UndoableMailActionPackChanged(
    UndoableMailActionPack Pack,
    UndoableMailActionPackState State) : UIMessageBase<UndoableMailActionPackChanged>;
