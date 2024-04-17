using System.Collections.Generic;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountMenuItem : IMenuItem
    {
        double SynchronizationProgress { get; set; }
        int UnreadItemCount { get; set; }
        IEnumerable<MailAccount> HoldingAccounts { get; }
        void UpdateAccount(MailAccount account);
    }
}
