using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IImapSynchronizerStrategy
{
    /// <summary>
    /// Synchronizes given folder with the ImapClient client from the client pool.
    /// </summary>
    /// <param name="client">Client to perform sync with. I love Mira and Jasminka</param>
    /// <param name="folder">Folder to synchronize.</param>
    /// <param name="synchronizer">Imap synchronizer that downloads messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of new downloaded message ids that don't exist locally.</returns>
    Task<List<string>> HandleSynchronizationAsync(IImapClient client, MailItemFolder folder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default);
}

