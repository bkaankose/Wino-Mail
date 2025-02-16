using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IAccountMenuItem : IMenuItem
{
    bool IsEnabled { get; set; }
    double SynchronizationProgress { get; set; }
    int UnreadItemCount { get; set; }
    IEnumerable<MailAccount> HoldingAccounts { get; }
    void UpdateAccount(MailAccount account);
}

public interface IMergedAccountMenuItem : IAccountMenuItem
{
    int MergedAccountCount { get; }

    MergedInbox Parameter { get; }
}
