namespace Wino.Core.Domain.Models.MailItem;

public sealed record MailCopyStateUpdate(string MailCopyId, bool? IsRead = null, bool? IsFlagged = null);
