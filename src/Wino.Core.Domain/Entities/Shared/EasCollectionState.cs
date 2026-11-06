using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class EasCollectionState
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public Guid AccountId { get; set; }
    public string RemoteId { get; set; } = string.Empty;
    public string ParentId { get; set; } = "0";
    public string DisplayName { get; set; } = string.Empty;
    public int FolderType { get; set; }
    public string SyncKey { get; set; } = "0";
    public string ResetGeneration { get; set; } = string.Empty;
}

public sealed class EasItemShadow
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public Guid AccountId { get; set; }
    [Indexed]
    public string CollectionId { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public string ApplicationData { get; set; } = string.Empty;
    public string Generation { get; set; } = string.Empty;
}
