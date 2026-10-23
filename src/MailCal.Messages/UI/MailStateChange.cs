using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public sealed record MailStateChange(Guid UniqueId, bool? IsRead = null, bool? IsFlagged = null)
{
    public MailCopyChangeFlags ChangedProperties =>
        (IsRead.HasValue ? MailCopyChangeFlags.IsRead : MailCopyChangeFlags.None) |
        (IsFlagged.HasValue ? MailCopyChangeFlags.IsFlagged : MailCopyChangeFlags.None);
}
