using System;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface IWinoSynchronizerFactory : IInitializeAsync
    {
        IBaseSynchronizer GetAccountSynchronizer(Guid accountId);
        IBaseSynchronizer CreateNewSynchronizer(MailAccount account);
    }
}
