using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Integration;
using Wino.Services.Extensions;
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

                // There are some changes on new messages or flag changes.
                // Deletions are tracked separately because some servers do not increase
                // the MODSEQ value for deleted messages.
                if (remoteHighestModSeq > localHighestModSeq)
                {
                    // Search for emails with a MODSEQ greater than the last known value
                    var changedUids = await remoteFolder.SearchAsync(SearchQuery.ChangedSince(localHighestModSeq)).ConfigureAwait(false);

                    // Get locally exists mails for the returned UIDs.
                    var existingMails = await MailService.GetExistingMailsAsync(folder.Id, changedUids);
                    var existingMailUids = existingMails.Select(m => MailkitClientExtensions.ResolveUidStruct(m.Id)).ToArray();

                    // These are the non-existing mails. They will be downloaded + processed.
                    var newMessageIds = changedUids.Except(existingMailUids).ToList();

                    // Fetch minimum data for the existing mails in one query.
                    var existingFlagData = await remoteFolder.FetchAsync(existingMailUids, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId).ConfigureAwait(false);

                    foreach (var update in existingFlagData)
                    {
                        if (update.UniqueId == null)
                        {
                            Log.Warning($"Couldn't fetch UniqueId for the mail. FetchAsync failed.");
                            continue;
                        }

                        if (update.Flags == null)
                        {
                            Log.Warning($"Couldn't fetch flags for the mail with UID {update.UniqueId.Id}. FetchAsync failed.");
                            continue;
                        }

                        var existingMail = existingMails.FirstOrDefault(m => MailkitClientExtensions.ResolveUidStruct(m.Id).Id == update.UniqueId.Id);

                        if (existingMail == null)
                        {
                            Log.Warning($"Couldn't find the mail with UID {update.UniqueId.Id} in the local database. Flag update is ignored.");
                            continue;
                        }

                        await HandleMessageFlagsChangeAsync(existingMail, update.Flags.Value).ConfigureAwait(false);
                    }

                    // Fetch the new mails.
                    var summaries = await remoteFolder.FetchAsync(newMessageIds, MailSynchronizationFlags, cancellationToken).ConfigureAwait(false);

                    foreach (var summary in summaries)
                    {
                        var mimeMessage = await remoteFolder.GetMessageAsync(summary.UniqueId, cancellationToken).ConfigureAwait(false);

                        var creationPackage = new ImapMessageCreationPackage(summary, mimeMessage);

                        var mailPackages = await synchronizer.CreateNewMailPackagesAsync(creationPackage, folder, cancellationToken).ConfigureAwait(false);

                        if (mailPackages != null)
                        {
                            foreach (var package in mailPackages)
                            {
                                // Local draft is mapped. We don't need to create a new mail copy.
                                if (package == null) continue;

                                bool isCreatedNew = await MailService.CreateMailAsync(folder.MailAccountId, package).ConfigureAwait(false);

                                // This is upsert. We are not interested in updated mails.
                                if (isCreatedNew) downloadedMessageIds.Add(package.Copy.Id);
                            }
                        }
                    }

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
                if (remoteFolder != null)
                {
                    if (remoteFolder.IsOpen)
                    {
                        await remoteFolder.CloseAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
