using System;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>
/// When account's special folder configuration is updated.
/// </summary>
public record AccountFolderConfigurationUpdated(Guid AccountId) : UIMessageBase<AccountFolderConfigurationUpdated>;
