using System;

namespace Wino.Messaging.Client.Accounts
{
    /// <summary>
    /// When account's special folder configuration is updated.
    /// </summary>
    public record AccountFolderConfigurationUpdated(Guid AccountId);
}
