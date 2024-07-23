using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISynchronizerFactory
    {
        IBaseSynchronizer GetAccountSynchronizer(Guid accountId);
        Task InitializeAsync();
    }
}
