using Wino.Core.Domain.Interfaces;

namespace Wino.Messages.Server
{
    public record DraftMapped(string LocalDraftCopyId, string RemoteDraftCopyId) : IServerMessage;
}
