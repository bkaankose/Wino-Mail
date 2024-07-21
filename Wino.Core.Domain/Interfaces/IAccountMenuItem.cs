using System.Collections.Generic;
using Wino.Domain.Entities;

namespace Wino.Domain.Interfaces
{
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
}
