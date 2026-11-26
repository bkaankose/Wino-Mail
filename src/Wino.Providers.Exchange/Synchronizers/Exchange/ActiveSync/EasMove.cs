namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed record EasMove(string SourceFolderId, string SourceId, string DestinationFolderId);

/// <summary>The MoveItems answer for one message: status 3 is success, 1 an invalid source collection or item id.</summary>
internal sealed record EasMoveResult(EasMove Move, string Status, string? DestinationId)
{
    internal bool Succeeded => Status == "3";

    /// <summary>
    /// Status 1, and after the folder hierarchy and the source folder were synchronized the message is no longer in
    /// the source folder: it is gone or was moved elsewhere, and Wino already shows that.
    /// </summary>
    internal bool IsGone { get; init; }
}
