using System;
using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

public interface ISynchronizerFactory
{
    Task<IMailCalSynchronizerBase> GetAccountSynchronizerAsync(Guid accountId);
    Task InitializeAsync();
    Task DeleteSynchronizerAsync(Guid accountId);
}
