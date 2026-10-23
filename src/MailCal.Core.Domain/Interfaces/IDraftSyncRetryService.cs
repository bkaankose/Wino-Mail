using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Core.Domain.Interfaces;

public interface IDraftSyncRetryService
{
    Task<bool> QueueEligibleRetriesAsync(Guid accountId, IMailCalSynchronizerBase synchronizer);

    Task RetryNowAsync(MailCopy draftCopy);
}
