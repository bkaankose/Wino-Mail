using System;

namespace Wino.Messaging.Server
{
    public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : ServerMessageBase<MergedInboxRenamed>;
}
