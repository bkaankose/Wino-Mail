namespace Wino.Messaging.UI;

public record DraftMapped(string LocalDraftCopyId, string RemoteDraftCopyId) : UIMessageBase<DraftMapped>;
