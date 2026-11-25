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

/// <summary>The server's answer to one command; <see cref="ServerId"/> is the new item's id for an Add.</summary>
internal sealed record EasMutationResult(EasMutation Mutation, string Status, string? ServerId)
{
    internal bool Succeeded => Status == "1";

    /// <summary>MS-ASCMD status 8: the item is no longer on the server.</summary>
    internal bool IsNotFound => Status == "8";
}
