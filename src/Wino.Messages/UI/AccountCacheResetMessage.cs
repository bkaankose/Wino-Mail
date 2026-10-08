using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

// Raised when the account's mail cache is reset.
public record AccountCacheResetMessage(Guid AccountId, AccountCacheResetReason Reason) : UIMessageBase<AccountCacheResetMessage>;
