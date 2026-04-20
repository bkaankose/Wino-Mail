using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public sealed record MailStateChange(Guid UniqueId, bool? IsRead = null, bool? IsFlagged = null)
{
    public MailCopyChangeFlags ChangedProperties =>
        (IsRead.HasValue ? MailCopyChangeFlags.IsRead : MailCopyChangeFlags.None) |
        (IsFlagged.HasValue ? MailCopyChangeFlags.IsFlagged : MailCopyChangeFlags.None);
}
