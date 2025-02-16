using System;

namespace Wino.Messaging.UI
{
    /// <summary>
    /// When account's special folder configuration is updated.
    /// </summary>
    public record AccountFolderConfigurationUpdated(Guid AccountId) : UIMessageBase<AccountFolderConfigurationUpdated>;
}
