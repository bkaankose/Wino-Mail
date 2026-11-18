using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

public interface IImapSynchronizer
{
    uint InitialMessageDownloadCountPerFolder { get; }

    /// <summary>
    /// The IDLE loop state. Changes are also published as <c>ImapIdleStateChanged</c> through the default messenger.
    /// </summary>
    ImapIdleStatus IdleStatus { get; }

    Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(ImapMessageCreationPackage message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);
    Task StartIdleClientAsync();
    Task StopIdleClientAsync();
    Task PreWarmClientPoolAsync();

    /// <summary>
    /// Stops IDLE and closes unused connections after the account starts needing attention.
    /// </summary>
    Task SuspendNetworkAccessAsync();
}
