namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed record EasMove(string SourceFolderId, string SourceId, string DestinationFolderId);

/// <summary>The MoveItems answer for one message: status 3 is success, 1 means the source item is gone.</summary>
internal sealed record EasMoveResult(EasMove Move, string Status, string? DestinationId)
{
    internal bool Succeeded => Status == "3";

    internal bool IsNotFound => Status == "1";
}
