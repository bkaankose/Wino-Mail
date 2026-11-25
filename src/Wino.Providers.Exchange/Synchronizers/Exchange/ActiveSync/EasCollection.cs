using System;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// One ActiveSync collection with its sync state. Mail collections are MailItemFolder rows, every other collection
/// is an EasCollectionState row; the engine reads both through this view and writes the state back to its table.
/// </summary>
internal sealed class EasCollection
{
    private EasCollection(Guid id, string serverId, int type, string name, string syncKey, string generation, bool isMail)
    {
        Id = id;
        ServerId = serverId;
        Type = type;
        Name = name;
        SyncKey = string.IsNullOrEmpty(syncKey) ? "0" : syncKey;
        Generation = generation ?? string.Empty;
        IsMail = isMail;
    }

    internal static EasCollection From(MailItemFolder folder)
        => new(folder.Id, folder.RemoteFolderId ?? string.Empty, folder.EasFolderType, folder.FolderName ?? string.Empty,
            folder.DeltaToken, folder.EasSyncGeneration, true);

    internal static EasCollection From(EasCollectionState state)
        => new(state.Id, state.ServerId, state.Type, state.DisplayName, state.SyncKey, state.Generation, false);

    internal Guid Id { get; }
    internal string ServerId { get; }
    internal int Type { get; }
    internal string Name { get; }
    internal bool IsMail { get; }
    internal string SyncKey { get; set; }
    internal string Generation { get; set; }
}
