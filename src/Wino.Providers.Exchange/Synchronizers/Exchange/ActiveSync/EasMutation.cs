using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal enum EasItemOperation
{
    Add,
    Change,
    Delete
}

/// <summary>One Sync command. <see cref="Id"/> is the ClientId of an Add and the ServerId of a Change or Delete.</summary>
internal sealed record EasMutation(EasItemOperation Operation, string Id, XElement? Data = null)
{
    /// <summary>
    /// For a Change whose own data does not describe the stored copy, the application data the copy takes once the
    /// server accepts it (<see cref="EasCommandEffect"/>).
    /// </summary>
    internal XElement? Effect { get; init; }

    internal static EasMutation Add(string clientId, XElement data) => new(EasItemOperation.Add, clientId, data);

    internal static EasMutation Change(string serverId, XElement data) => new(EasItemOperation.Change, serverId, data);

    internal static EasMutation Delete(string serverId) => new(EasItemOperation.Delete, serverId);

    internal XElement ToCommand()
    {
        var sync = EasDataMapper.Sync;
        var command = new XElement(sync + Operation.ToString(),
            new XElement(sync + (Operation == EasItemOperation.Add ? "ClientId" : "ServerId"), Id));
        if (Data != null && Operation != EasItemOperation.Delete)
            command.Add(new XElement(Data));
        return command;
    }
}

/// <summary>
/// The server's answer to one command; <see cref="ServerId"/> is the new item's id for an Add, and
/// <see cref="Data"/> the application data the server returns with it (for mail, its ConversationId).
/// </summary>
internal sealed record EasMutationResult(EasMutation Mutation, string Status, string? ServerId, XElement? Data = null)
{
    internal bool Succeeded => Status == "1";

    /// <summary>MS-ASCMD status 8: the item or collection id is no longer valid on the server.</summary>
    internal bool IsNotFound => Status == "8";

    /// <summary>
    /// Status 8, and the synchronization that followed removed the item: it is gone from the server and from Wino.
    /// </summary>
    internal bool IsGone { get; init; }
}
