using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services.Models.MailItem;

namespace Wino.Services.Interfaces;

public interface IDraftUpdateCoordinator : IAsyncDisposable
{
    void Schedule(DraftUpdateSnapshot snapshot);
    Task<MailCopy> StopAsync(Guid accountId, Guid uniqueId);
    Task StopAccountAsync(Guid accountId);
}
