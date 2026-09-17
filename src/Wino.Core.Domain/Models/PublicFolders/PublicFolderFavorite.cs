using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// A public folder the user pinned for quick access. This is the only persisted public folder state; the
/// folder hierarchy and content remain fetched live. Stored as a small JSON list in the configuration store.
/// </summary>
public sealed class PublicFolderFavorite
{
    public Guid AccountId { get; set; }

    /// <summary>The provider's id of the public folder.</summary>
    public string FolderId { get; set; }

    public PublicFolderKind Kind { get; set; }

    public string Name { get; set; }

    /// <summary>Overlay colour for a calendar favourite (unused for the other kinds).</summary>
    public string ColorHex { get; set; }
}
