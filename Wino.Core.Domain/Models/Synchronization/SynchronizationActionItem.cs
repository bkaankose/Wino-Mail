using System;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// Represents a single grouped synchronization action displayed in the sync status flyout.
/// For example: "Deleting 3 mail(s)" or "Marking folder as read".
/// </summary>
public class SynchronizationActionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string AccountName { get; set; }
    public string Description { get; set; }
}
