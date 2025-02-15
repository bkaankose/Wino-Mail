using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces;

public interface IImapSynchronizer
{
    uint InitialMessageDownloadCountPerFolder { get; }

    Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(ImapMessageCreationPackage message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default);
    Task StartIdleClientAsync();
    Task StopIdleClientAsync();
}
