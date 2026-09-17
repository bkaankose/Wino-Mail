using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// A single node in a read-only remote folder tree (Exchange public folders or the online archive).
/// Metadata only and never persisted: the tree is walked lazily (children fetched on expand) and content
/// is fetched live when a folder is opened.
/// </summary>
public sealed class PublicFolderNode
{
    /// <summary>The provider's id of this folder (an EWS folder id, or a "mapi:" folder id over MAPI).</summary>
    public string Id { get; init; }

    /// <summary>The provider's id of the parent, or null for a direct child of the tree root.</summary>
    public string ParentId { get; init; }

    public string Name { get; init; }

    /// <summary>Derived from the folder's container class; decides which surface shows the folder.</summary>
    public PublicFolderKind Kind { get; init; }

    /// <summary>Whether the folder has sub-folders, so the navigation shows an expander.</summary>
    public bool HasChildren { get; init; }
}
