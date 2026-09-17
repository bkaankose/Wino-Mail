using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// A per-account safe/blocked sender entry, the app's own junk list. <see cref="Address"/> is a
/// lower-cased email address or a bare domain.
/// </summary>
public class JunkSender
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public string Address { get; set; }

    public JunkListType ListType { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
