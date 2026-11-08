using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// The one directory photo lookup made for a sender address of an Exchange account. A row without a picture is a
/// lookup that found none, so the address is never asked about again.
/// </summary>
public class DirectoryPhotoLookup
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid MailAccountId { get; set; }
    [Indexed] public string NormalizedAddress { get; set; }
    public Guid? PictureFileId { get; set; }
    public DateTime LookedUpAtUtc { get; set; } = DateTime.UtcNow;
}
