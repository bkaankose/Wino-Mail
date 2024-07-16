using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messages.Server
{
    public record MergedInboxRenamed(Guid MergedInboxId, string NewName) : IServerMessage;
}
