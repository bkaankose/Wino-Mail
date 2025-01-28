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

namespace Wino.Core.Synchronizers.ImapSync
{
    /// <summary>
    /// RFC 4551 CONDSTORE IMAP Synchronization strategy.
    /// </summary>
    internal class CondstoreSynchronizer : ImapSynchronizationStrategyBase
    {
        public CondstoreSynchronizer(IFolderService folderService, IMailService mailService) : base(folderService, mailService)
        {
        }

        public async override Task<List<string>> HandleSynchronizationAsync(IImapClient client,
                                                                              MailItemFolder folder,
                                                                              IImapSynchronizer synchronizer,
                                                                              CancellationToken cancellationToken = default)
        {
            if (client is not WinoImapClient winoClient)
                throw new ArgumentException("Client must be of type WinoImapClient.", nameof(client));

            if (!client.Capabilities.HasFlag(ImapCapabilities.CondStore))
                throw new ImapSynchronizerStrategyException("Server does not support CONDSTORE.");

            IMailFolder remoteFolder = null;

            var downloadedMessageIds = new List<string>();

            try
            {
                remoteFolder = await winoClient.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);

                await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                var localHighestModSeq = (ulong)folder.HighestModeSeq;
                var remoteHighestModSeq = remoteFolder.HighestModSeq;

                bool isInitialSynchronization = localHighestModSeq == 0;

                // There are some changes on new messages or flag changes.
                // Deletions are tracked separately because some servers do not increase
                // the MODSEQ value for deleted messages.
                if (remoteHighestModSeq > localHighestModSeq)
                {
                    var changedUids = await GetChangedUidsAsync(client, folder, remoteFolder, synchronizer, cancellationToken).ConfigureAwait(false);

                    // Get locally exists mails for the returned UIDs.
                    downloadedMessageIds = await HandleChangedUIdsAsync(folder, synchronizer, remoteFolder, changedUids, cancellationToken).ConfigureAwait(false);

                    folder.HighestModeSeq = (long)remoteHighestModSeq;

                    await FolderService.UpdateFolderAsync(folder).ConfigureAwait(false);
                }

                await ManageUUIdBasedDeletedMessagesAsync(folder, remoteFolder, cancellationToken).ConfigureAwait(false);

                return downloadedMessageIds;
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
        }

        internal override async Task<IList<UniqueId>> GetChangedUidsAsync(IImapClient winoClient, MailItemFolder localFolder, IMailFolder remoteFolder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default)
        {
            var localHighestModSeq = (ulong)localFolder.HighestModeSeq;
            var remoteHighestModSeq = remoteFolder.HighestModSeq;

            // Search for emails with a MODSEQ greater than the last known value.
            // Use SORT extension if server supports.

            IList<UniqueId> changedUids = null;

            // TODO: Temporarily disabled.
            //if (winoClient.Capabilities.HasFlag(ImapCapabilities.Sort))
            //{
            //    changedUids = await remoteFolder.SortAsync(SearchQuery.ChangedSince(localHighestModSeq), [OrderBy.ReverseDate], cancellationToken).ConfigureAwait(false);
            //}
            //else
            //{
            //    changedUids = await remoteFolder.SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken).ConfigureAwait(false);
            //}

            changedUids = await remoteFolder.SearchAsync(SearchQuery.ChangedSince(localHighestModSeq), cancellationToken).ConfigureAwait(false);

            // For initial synchronizations, take the first allowed number of items.
            // For consequtive synchronizations, take all the items. We don't want to miss any changes.
            // Smaller uid means newer message. For initial sync, we need start taking items from the top.

            bool isInitialSynchronization = localHighestModSeq == 0;

            if (isInitialSynchronization)
            {
                changedUids = changedUids.OrderByDescending(a => a.Id).Take((int)synchronizer.InitialMessageDownloadCountPerFolder).ToList();
            }

            return changedUids;
        }
    }
}
