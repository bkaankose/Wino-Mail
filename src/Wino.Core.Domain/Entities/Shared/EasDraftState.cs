using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// What Wino keeps about a draft in an Exchange ActiveSync Drafts folder: the attachments it added to the server copy,
/// by the FileReference the server returned for each, and whether the server copy still has to be deleted because the
/// message was sent.
/// </summary>
public sealed class EasDraftState
{
    /// <summary>Account, collection server id and the draft's ServerId.</summary>
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public Guid AccountId { get; set; }
    [Indexed]
    public Guid CollectionId { get; set; }
    public string ServerId { get; set; } = string.Empty;

    /// <summary>The recorded attachments as XML: FileReference and the fingerprint of the content Wino sent.</summary>
    public string Attachments { get; set; } = string.Empty;

    /// <summary>The message was sent and the server copy of the draft is deleted at the next opportunity.</summary>
    public bool DeletePending { get; set; }
}
