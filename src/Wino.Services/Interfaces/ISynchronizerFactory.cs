using System;
using System.Threading.Tasks;

namespace Wino.Services.Interfaces;

public interface ISynchronizerFactory
{
    Task<IWinoSynchronizerBase> GetAccountSynchronizerAsync(Guid accountId);
    Task InitializeAsync();
    Task DeleteSynchronizerAsync(Guid accountId);
}
