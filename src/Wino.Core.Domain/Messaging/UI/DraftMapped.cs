namespace Wino.Core.Domain.Messaging.UI;

public record DraftMapped(string LocalDraftCopyId, string RemoteDraftCopyId) : UIMessageBase<DraftMapped>;
