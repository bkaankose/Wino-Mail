using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;

namespace Wino.Core.Synchronizers.ImapSync;

/// <summary>
/// Uid based IMAP Synchronization strategy.
/// </summary>
internal class UidBasedSynchronizer : ImapSynchronizationStrategyBase
{
    public UidBasedSynchronizer(IFolderService folderService, Domain.Interfaces.IMailService mailService) : base(folderService, mailService)
    {
    }

    public override async Task<List<string>> HandleSynchronizationAsync(IImapClient client, MailItemFolder folder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default)
    {
        if (client is not WinoImapClient winoClient)
            throw new ArgumentException("Client must be of type WinoImapClient.", nameof(client));

        Folder = folder;

        var downloadedMessageIds = new List<string>();
        IMailFolder remoteFolder = null;

        try
        {
            remoteFolder = await winoClient.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);

            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            // Fetch UIDs from the remote folder
            var remoteUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);

            remoteUids = remoteUids.OrderByDescending(a => a.Id).Take((int)synchronizer.InitialMessageDownloadCountPerFolder).ToList();

            await HandleChangedUIdsAsync(synchronizer, remoteFolder, remoteUids, cancellationToken).ConfigureAwait(false);
            await ManageUUIdBasedDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (FolderNotFoundException)
        {
            await FolderService.DeleteFolderAsync(folder.MailAccountId, folder.RemoteFolderId).ConfigureAwait(false);

            return default;
        }
        catch (Exception)
        {

            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (remoteFolder != null)
                {
                    if (remoteFolder.IsOpen)
                    {
                        await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        return downloadedMessageIds;
    }

    internal override Task<IList<UniqueId>> GetChangedUidsAsync(IImapClient client, IMailFolder remoteFolder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
