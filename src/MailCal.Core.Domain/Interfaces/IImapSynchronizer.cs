using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Domain.Interfaces;

public interface IImapSynchronizer
{
    uint InitialMessageDownloadCountPerFolder { get; }

    Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(ImapMessageCreationPackage message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);
    Task StartIdleClientAsync();
    Task StopIdleClientAsync();
    Task PreWarmClientPoolAsync();

    /// <summary>
    /// Stops IDLE and closes unused connections after the account starts needing attention.
    /// </summary>
    Task SuspendNetworkAccessAsync();
}
