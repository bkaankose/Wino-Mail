using System;

namespace Wino.Messaging.UI
{
    public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : UIMessageBase<MergedInboxRenamed>;
}
