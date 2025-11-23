using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Synchronizers.ImapSync;

/// <summary>
/// RFC 5162 QRESYNC IMAP Synchronization strategy.
/// </summary>
internal class QResyncSynchronizer : ImapSynchronizationStrategyBase
{
    public QResyncSynchronizer(IFolderService folderService, IMailService mailService) : base(folderService, mailService)
    {
    }

    public override async Task<List<string>> HandleSynchronizationAsync(IImapClient client,
                                                                        MailItemFolder folder,
                                                                        IImapSynchronizer synchronizer,
                                                                        CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        if (client is not WinoImapClient winoClient)
            throw new ImapSynchronizerStrategyException("Client must be of type WinoImapClient.");

        if (!client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
            throw new ImapSynchronizerStrategyException("Server does not support QRESYNC.");

        if (!winoClient.IsQResyncEnabled)
            throw new ImapSynchronizerStrategyException("QRESYNC is not enabled for WinoImapClient.");

        // Ready to implement QRESYNC synchronization.

        IMailFolder remoteFolder = null;

        Folder = folder;

        try
        {
            remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);

            // Check the Uid validity first.
            // If they don't match, clear all the local data and perform full-resync.

            bool isCacheValid = remoteFolder.UidValidity == folder.UidValidity;

            if (!isCacheValid)
            {
                // TODO: Remove all local data.
            }

            // Perform QRESYNC synchronization.
            var localHighestModSeq = (ulong)folder.HighestModeSeq;
            // HIGHESTMODSEQ must be a positive integer, 0 is illegal.
            // It's harmless to set it to 1, as RFC-compliant server without mod-seq would ignore this parameter.
            if (localHighestModSeq == 0) localHighestModSeq = 1;

            remoteFolder.MessagesVanished += OnMessagesVanished;
            remoteFolder.MessageFlagsChanged += OnMessageFlagsChanged;

            var allUids = await FolderService.GetKnownUidsForFolderAsync(folder.Id);
            var allUniqueIds = allUids.Select(a => new UniqueId(a)).ToList();

            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, folder.UidValidity, localHighestModSeq, allUniqueIds).ConfigureAwait(false);

            var changedUids = await GetChangedUidsAsync(client, remoteFolder, synchronizer, cancellationToken).ConfigureAwait(false);

            downloadedMessageIds = await HandleChangedUIdsAsync(synchronizer, remoteFolder, changedUids, cancellationToken).ConfigureAwait(false);

            // Update the local folder with the new highest mod-seq and validity.
            folder.HighestModeSeq = unchecked((long)remoteFolder.HighestModSeq);
            folder.UidValidity = remoteFolder.UidValidity;

            await ManageUUIdBasedDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);

            await FolderService.UpdateFolderAsync(folder).ConfigureAwait(false);
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
                    remoteFolder.MessagesVanished -= OnMessagesVanished;
                    remoteFolder.MessageFlagsChanged -= OnMessageFlagsChanged;

                    if (remoteFolder.IsOpen)
                    {
                        await remoteFolder.CloseAsync();
                    }
                }
            }
        }

        return downloadedMessageIds;
    }

    internal override async Task<IList<UniqueId>> GetChangedUidsAsync(IImapClient client, IMailFolder remoteFolder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default)
    {
        var localHighestModSeq = (ulong)Folder.HighestModeSeq;
        if (localHighestModSeq == 0) localHighestModSeq = 1;
        return await remoteFolder.SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken).ConfigureAwait(false);
    }
}
