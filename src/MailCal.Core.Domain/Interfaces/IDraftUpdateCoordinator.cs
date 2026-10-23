using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Domain.Interfaces;

public interface IDraftUpdateCoordinator : IAsyncDisposable
{
    void Schedule(DraftUpdateSnapshot snapshot);
    Task<MailCopy> StopAsync(Guid accountId, Guid uniqueId);
    Task StopAccountAsync(Guid accountId);
}
