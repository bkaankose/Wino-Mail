using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// The server's answer to an Exchange ActiveSync Add that reached the server in a request sent again after its response
/// was lost. It is kept by ClientId until the operation that owns the item asks again, so the item is not added twice.
/// </summary>
public sealed class EasClientAnswer
{
    /// <summary>Account, collection server id and ClientId.</summary>
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public Guid AccountId { get; set; }
    [Indexed]
    public Guid CollectionId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;

    /// <summary>The ApplicationData the server returned with the answer, as XML; empty when none.</summary>
    public string ApplicationData { get; set; } = string.Empty;
}
