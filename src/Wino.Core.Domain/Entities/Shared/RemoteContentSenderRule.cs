using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A per-sender exception to the remote content mode. Rules are global: they apply on every account on the device.
/// </summary>
public class RemoteContentSenderRule
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Lower-case address or bare domain, normalized by <see cref="Models.Reader.RemoteContentRulePattern"/>.
    /// </summary>
    [Indexed(Unique = true)] public string Pattern { get; set; } = string.Empty;
    public RemoteContentRuleKind Kind { get; set; }
    public RemoteContentSenderPolicy Policy { get; set; }
    public RemoteContentRuleSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastAppliedUtc { get; set; }
}
