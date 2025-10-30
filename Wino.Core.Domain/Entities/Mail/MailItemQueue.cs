using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

public class MailItemQueue
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string RemoteServerId { get; set; }
    public string RemoteFolderId { get; set; }  // For Outlook per-folder sync
    public bool IsProcessed { get; set; }
    public int FailedCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }

    public bool IsRecent() => (DateTime.UtcNow - CreatedAt).TotalDays <= 7;
    public bool ShouldDelete() => IsProcessed || FailedCount >= 30;
}
