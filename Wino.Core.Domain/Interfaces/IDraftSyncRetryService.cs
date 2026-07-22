using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IDraftSyncRetryService
{
    Task<bool> QueueEligibleRetriesAsync(Guid accountId, IWinoSynchronizerBase synchronizer);

    Task RetryNowAsync(MailCopy draftCopy);
}
