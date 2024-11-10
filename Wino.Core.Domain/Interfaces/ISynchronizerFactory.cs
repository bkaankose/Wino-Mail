using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISynchronizerFactory
    {
        Task<IBaseMailSynchronizer> GetAccountSynchronizerAsync(Guid accountId);
        Task InitializeAsync();
    }
}
