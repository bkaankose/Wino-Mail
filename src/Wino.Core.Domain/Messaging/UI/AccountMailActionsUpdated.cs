using System;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>
/// An account's hover or swipe action overrides changed in settings.
/// </summary>
public record AccountMailActionsUpdated(Guid AccountId) : UIMessageBase<AccountMailActionsUpdated>;
