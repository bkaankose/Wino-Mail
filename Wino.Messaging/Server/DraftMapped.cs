namespace Wino.Messaging.Server
{
    public record DraftMapped(string LocalDraftCopyId, string RemoteDraftCopyId) : ServerMessageBase<DraftMapped>;
}
