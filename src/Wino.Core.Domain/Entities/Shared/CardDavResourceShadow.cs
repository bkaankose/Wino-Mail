using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// One vCard resource known on the server: where it lives, the version last applied
/// locally and the local row it maps to. A contact maps to <see cref="ContactId"/>, a
/// group vCard to <see cref="ListId"/>.
/// </summary>
public sealed class CardDavResourceShadow
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AddressBookId { get; set; }
    public string ExactHref { get; set; }
    public string ETag { get; set; }
    public string Uid { get; set; }
    public CardDavResourceKind Kind { get; set; }
    [Indexed] public Guid? ContactId { get; set; }
    [Indexed] public Guid? ListId { get; set; }

    /// <summary>
    /// Member UIDs of a group, one per line. Members are kept by UID because a group can
    /// reference a contact that has not been downloaded yet.
    /// </summary>
    public string MemberUids { get; set; }
}
