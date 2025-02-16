using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.Client.Shell
{
    /// <summary>
    /// When 
    /// - There is no selection of any folder for any account
    /// - Multiple accounts exists
    /// - User clicked 'Create New Mail'
    /// 
    /// flyout must be presented to pick correct account.
    /// This message will be picked up by UWP Shell.
    /// </summary>
    public record CreateNewMailWithMultipleAccountsRequested(IEnumerable<MailAccount> AllAccounts);
}
