using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

// Raised when the account's mail cache is reset.
public record AccountCacheResetMessage(Guid AccountId, AccountCacheResetReason Reason) : UIMessageBase<AccountCacheResetMessage>;
