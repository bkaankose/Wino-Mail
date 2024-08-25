using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions.Extensions;
using MimeKit;
using MoreLinq;
using Serilog;
using SqlKata;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Comparers;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Extensions;
using Wino.Messaging.UI;

namespace Wino.Core.Services
{
    public class MailService : BaseDatabaseService, IMailService
    {
        private const int ItemLoadCount = 100;

        private readonly IFolderService _folderService;
        private readonly IContactService _contactService;
        private readonly IAccountService _accountService;
        private readonly ISignatureService _signatureService;
        private readonly IThreadingStrategyProvider _threadingStrategyProvider;
        private readonly IMimeFileService _mimeFileService;
        private readonly IPreferencesService _preferencesService;

        private readonly ILogger _logger = Log.ForContext<MailService>();

        public MailService(IDatabaseService databaseService,
                           IFolderService folderService,
                           IContactService contactService,
                           IAccountService accountService,
                           ISignatureService signatureService,
                           IThreadingStrategyProvider threadingStrategyProvider,
                           IMimeFileService mimeFileService,
                           IPreferencesService preferencesService) : base(databaseService)
        {
            _folderService = folderService;
            _contactService = contactService;
            _accountService = accountService;
            _signatureService = signatureService;
            _threadingStrategyProvider = threadingStrategyProvider;
            _mimeFileService = mimeFileService;
            _preferencesService = preferencesService;
        }

        public async Task<(MailCopy draftMailCopy, string draftBase64MimeMessage)> CreateDraftAsync(Guid accountId, DraftCreationOptions draftCreationOptions)
        {
            var composerAccount = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            var createdDraftMimeMessage = await CreateDraftMimeAsync(composerAccount, draftCreationOptions);

            var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(composerAccount.Id, SpecialFolderType.Draft);

            // Get locally created unique id from the mime headers.
            // This header will be used to map the local draft copy with the remote draft copy.
            var mimeUniqueId = createdDraftMimeMessage.Headers[Constants.WinoLocalDraftHeader];

            var primaryAlias = await _accountService.GetPrimaryAccountAliasAsync(accountId).ConfigureAwait(false);

            var copy = new MailCopy
            {
                UniqueId = Guid.Parse(mimeUniqueId),
                Id = Guid.NewGuid().ToString(), // This will be replaced after network call with the remote draft id.
                CreationDate = DateTime.UtcNow,
                FromAddress = primaryAlias?.AliasAddress ?? composerAccount.Address,
                FromName = composerAccount.SenderName,
                HasAttachments = false,
                Importance = MailImportance.Normal,
                Subject = createdDraftMimeMessage.Subject,
                PreviewText = createdDraftMimeMessage.TextBody,
                IsRead = true,
                IsDraft = true,
                FolderId = draftFolder.Id,
                DraftId = $"{Constants.LocalDraftStartPrefix}{Guid.NewGuid()}",
                AssignedFolder = draftFolder,
                AssignedAccount = composerAccount,
                FileId = Guid.NewGuid()
            };

            // If replying, add In-Reply-To, ThreadId and References.
            if (draftCreationOptions.ReferencedMessage != null)
            {
                if (draftCreationOptions.ReferencedMessage.MimeMessage.References != null)
                    copy.References = string.Join(",", draftCreationOptions.ReferencedMessage.MimeMessage.References);

                if (!string.IsNullOrEmpty(draftCreationOptions.ReferencedMessage.MimeMessage.MessageId))
                    copy.InReplyTo = draftCreationOptions.ReferencedMessage.MimeMessage.MessageId;

                if (!string.IsNullOrEmpty(draftCreationOptions.ReferencedMessage.MailCopy?.ThreadId))
                    copy.ThreadId = draftCreationOptions.ReferencedMessage.MailCopy.ThreadId;
            }

            await Connection.InsertAsync(copy);

            await _mimeFileService.SaveMimeMessageAsync(copy.FileId, createdDraftMimeMessage, composerAccount.Id);

            ReportUIChange(new DraftCreated(copy, composerAccount));

            return (copy, createdDraftMimeMessage.GetBase64MimeMessage());
        }

        public async Task<List<MailCopy>> GetMailsByFolderIdAsync(Guid folderId)
        {
            var mails = await Connection.QueryAsync<MailCopy>("SELECT * FROM MailCopy WHERE FolderId = ?", folderId);

            foreach (var mail in mails)
            {
                await LoadAssignedPropertiesAsync(mail).ConfigureAwait(false);
            }

            return mails;
        }

        public async Task<List<MailCopy>> GetUnreadMailsByFolderIdAsync(Guid folderId)
        {
            var unreadMails = await Connection.QueryAsync<MailCopy>("SELECT * FROM MailCopy WHERE FolderId = ? AND IsRead = 0", folderId);

            foreach (var mail in unreadMails)
            {
                await LoadAssignedPropertiesAsync(mail).ConfigureAwait(false);
            }

            return unreadMails;
        }

        private string BuildMailFetchQuery(MailListInitializationOptions options)
        {
            // If the search query is there, we should ignore some properties and trim it.
            //if (!string.IsNullOrEmpty(options.SearchQuery))
            //{
            //    options.IsFocusedOnly = null;
            //    filterType = FilterOptionType.All;

            //    searchQuery = searchQuery.Trim();
            //}

            // SQLite PCL doesn't support joins.
            // We make the query using SqlKatka and execute it directly on SQLite-PCL.

            var query = new Query("MailCopy")
                        .Join("MailItemFolder", "MailCopy.FolderId", "MailItemFolder.Id")
                        .WhereIn("MailCopy.FolderId", options.Folders.Select(a => a.Id))
                        .Take(ItemLoadCount)
                        .SelectRaw("MailCopy.*");

            if (options.SortingOptionType == SortingOptionType.ReceiveDate)
                query.OrderByDesc("CreationDate");
            else if (options.SortingOptionType == SortingOptionType.Sender)
                query.OrderBy("FromName");

            // Conditional where.
            switch (options.FilterType)
            {
                case FilterOptionType.Unread:
                    query.Where("MailCopy.IsRead", false);
                    break;
                case FilterOptionType.Flagged:
                    query.Where("MailCopy.IsFlagged", true);
                    break;
                case FilterOptionType.Files:
                    query.Where("MailCopy.HasAttachments", true);
                    break;
            }

            if (options.IsFocusedOnly != null)
                query.Where("MailCopy.IsFocused", options.IsFocusedOnly.Value);

            if (!string.IsNullOrEmpty(options.SearchQuery))
                query.Where(a =>
                            a.OrWhereContains("MailCopy.PreviewText", options.SearchQuery)
                            .OrWhereContains("MailCopy.Subject", options.SearchQuery)
                            .OrWhereContains("MailCopy.FromName", options.SearchQuery)
                            .OrWhereContains("MailCopy.FromAddress", options.SearchQuery));

            if (options.ExistingUniqueIds?.Any() ?? false)
            {
                query.WhereNotIn("MailCopy.UniqueId", options.ExistingUniqueIds);
            }

            //if (options.Skip > 0)
            //{
            //    query.Skip(options.Skip);
            //}

            return query.GetRawQuery();
        }

        public async Task<List<IMailItem>> FetchMailsAsync(MailListInitializationOptions options, CancellationToken cancellationToken = default)
        {
            var query = BuildMailFetchQuery(options);

            var mails = await Connection.QueryAsync<MailCopy>(query);

            Dictionary<Guid, MailItemFolder> folderCache = [];
            Dictionary<Guid, MailAccount> accountCache = [];
            Dictionary<string, AccountContact> contactCache = [];

            // Populate Folder Assignment for each single mail, to be able later group by "MailAccountId".
            // This is needed to execute threading strategy by account type.
            // Avoid DBs calls as possible, storing info in a dictionary.
            foreach (var mail in mails)
            {
                await LoadAssignedPropertiesWithCacheAsync(mail, folderCache, accountCache).ConfigureAwait(false);
            }

            // Remove items that has no assigned account or folder.
            mails.RemoveAll(a => a.AssignedAccount == null || a.AssignedFolder == null);

            if (!options.CreateThreads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Threading is disabled. Just return everything as it is.
                mails.Sort(options.SortingOptionType == SortingOptionType.ReceiveDate ? new DateComparer() : new NameComparer());

                return new List<IMailItem>(mails);
            }

            // Populate threaded items.

            var threadedItems = new List<IMailItem>();

            // Each account items must be threaded separately.
            foreach (var group in mails.GroupBy(a => a.AssignedAccount.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var accountId = group.Key;
                var groupAccount = mails.First(a => a.AssignedAccount.Id == accountId).AssignedAccount;

                var threadingStrategy = _threadingStrategyProvider.GetStrategy(groupAccount.ProviderType);

                // Only thread items from Draft and Sent folders must present here.
                // Otherwise this strategy will fetch the items that are in Deleted folder as well.
                var accountThreadedItems = await threadingStrategy.ThreadItemsAsync([.. group]);

                // Populate threaded items with folder and account assignments.
                // Almost everything already should be in cache from initial population.
                foreach (var mail in accountThreadedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await LoadAssignedPropertiesWithCacheAsync(mail, folderCache, accountCache).ConfigureAwait(false);
                }

                if (accountThreadedItems != null)
                {
                    threadedItems.AddRange(accountThreadedItems);
                }
            }

            threadedItems.Sort(options.SortingOptionType == SortingOptionType.ReceiveDate ? new DateComparer() : new NameComparer());
            cancellationToken.ThrowIfCancellationRequested();

            return threadedItems;

            // Recursive function to populate folder and account assignments for each mail item.
            async Task LoadAssignedPropertiesWithCacheAsync(IMailItem mail,
                                                            Dictionary<Guid, MailItemFolder> folderCache,
                                                            Dictionary<Guid, MailAccount> accountCache)
            {
                if (mail is ThreadMailItem threadMailItem)
                {
                    foreach (var childMail in threadMailItem.ThreadItems)
                    {
                        await LoadAssignedPropertiesWithCacheAsync(childMail, folderCache, accountCache).ConfigureAwait(false);
                    }
                }

                if (mail is MailCopy mailCopy)
                {
                    MailAccount accountAssignment = null;

                    var isFolderCached = folderCache.TryGetValue(mailCopy.FolderId, out MailItemFolder folderAssignment);
                    accountAssignment = null;
                    if (!isFolderCached)
                    {
                        folderAssignment = await _folderService.GetFolderAsync(mailCopy.FolderId).ConfigureAwait(false);
                        _ = folderCache.TryAdd(mailCopy.FolderId, folderAssignment);
                    }

                    if (folderAssignment != null)
                    {
                        var isAccountCached = accountCache.TryGetValue(folderAssignment.MailAccountId, out accountAssignment);
                        if (!isAccountCached)
                        {
                            accountAssignment = await _accountService.GetAccountAsync(folderAssignment.MailAccountId).ConfigureAwait(false);
                            _ = accountCache.TryAdd(folderAssignment.MailAccountId, accountAssignment);

                        }
                    }

                    bool isContactCached = contactCache.TryGetValue(mailCopy.FromAddress, out AccountContact contactAssignment);

                    if (!isContactCached && accountAssignment != null)
                    {
                        contactAssignment = await GetSenderContactForAccountAsync(accountAssignment, mailCopy.FromAddress).ConfigureAwait(false);

                        if (contactAssignment != null)
                        {
                            _ = contactCache.TryAdd(mailCopy.FromAddress, contactAssignment);
                        }
                    }

                    mailCopy.AssignedFolder = folderAssignment;
                    mailCopy.AssignedAccount = accountAssignment;
                    mailCopy.SenderContact = contactAssignment ?? new AccountContact() { Name = mailCopy.FromName, Address = mailCopy.FromAddress };
                }
            }
        }

        private async Task<List<MailCopy>> GetMailItemsAsync(string mailCopyId)
        {
            var mailCopies = await Connection.Table<MailCopy>().Where(a => a.Id == mailCopyId).ToListAsync();

            foreach (var mailCopy in mailCopies)
            {
                await LoadAssignedPropertiesAsync(mailCopy).ConfigureAwait(false);
            }

            return mailCopies;
        }

        private Task<AccountContact> GetSenderContactForAccountAsync(MailAccount account, string fromAddress)
        {
            // Make sure to return the latest up to date contact information for the original account.
            if (fromAddress == account.Address)
            {
                return Task.FromResult(new AccountContact() { Address = account.Address, Name = account.SenderName, Base64ContactPicture = account.Base64ProfilePictureData });
            }
            else
            {
                return _contactService.GetAddressInformationByAddressAsync(fromAddress);
            }
        }

        private async Task LoadAssignedPropertiesAsync(MailCopy mailCopy)
        {
            if (mailCopy == null) return;

            // Load AssignedAccount, AssignedFolder and SenderContact.

            var folder = await _folderService.GetFolderAsync(mailCopy.FolderId);

            if (folder == null) return;

            var account = await _accountService.GetAccountAsync(folder.MailAccountId);

            if (account == null) return;

            mailCopy.AssignedAccount = account;
            mailCopy.AssignedFolder = folder;
            mailCopy.SenderContact = await GetSenderContactForAccountAsync(account, mailCopy.FromAddress).ConfigureAwait(false);
        }

        public async Task<MailCopy> GetSingleMailItemWithoutFolderAssignmentAsync(string mailCopyId)
        {
            var mailCopy = await Connection.Table<MailCopy>().FirstOrDefaultAsync(a => a.Id == mailCopyId);

            if (mailCopy == null) return null;

            await LoadAssignedPropertiesAsync(mailCopy).ConfigureAwait(false);

            return mailCopy;
        }

        public async Task<MailCopy> GetSingleMailItemAsync(string mailCopyId, string remoteFolderId)
        {
            var query = new Query("MailCopy")
                            .Join("MailItemFolder", "MailCopy.FolderId", "MailItemFolder.Id")
                            .Where("MailCopy.Id", mailCopyId)
                            .Where("MailItemFolder.RemoteFolderId", remoteFolderId)
                            .SelectRaw("MailCopy.*")
                            .GetRawQuery();

            var mailItem = await Connection.FindWithQueryAsync<MailCopy>(query);

            if (mailItem == null) return null;

            await LoadAssignedPropertiesAsync(mailItem).ConfigureAwait(false);

            return mailItem;
        }

        public async Task<MailCopy> GetSingleMailItemAsync(Guid uniqueMailId)
        {
            var mailItem = await Connection.FindAsync<MailCopy>(uniqueMailId);

            if (mailItem == null) return null;

            await LoadAssignedPropertiesAsync(mailItem).ConfigureAwait(false);

            return mailItem;
        }

        // v2

        public async Task DeleteMailAsync(Guid accountId, string mailCopyId)
        {
            var allMails = await GetMailItemsAsync(mailCopyId).ConfigureAwait(false);

            foreach (var mailItem in allMails)
            {
                await DeleteMailInternalAsync(mailItem).ConfigureAwait(false);

                // Delete mime file.
                // Even though Gmail might have multiple copies for the same mail, we only have one MIME file for all.
                // Their FileId is inserted same.
                await _mimeFileService.DeleteMimeMessageAsync(accountId, mailItem.FileId);
            }
        }

        #region Repository Calls

        private async Task InsertMailAsync(MailCopy mailCopy)
        {
            if (mailCopy == null)
            {
                _logger.Warning("Null mail passed to InsertMailAsync call.");
                return;
            }

            if (mailCopy.FolderId == Guid.Empty)
            {
                _logger.Warning("Invalid FolderId for MailCopyId {Id} for InsertMailAsync", mailCopy.Id);
                return;
            }

            _logger.Debug("Inserting mail {MailCopyId} to {FolderName}", mailCopy.Id, mailCopy.AssignedFolder.FolderName);

            await Connection.InsertAsync(mailCopy).ConfigureAwait(false);

            ReportUIChange(new MailAddedMessage(mailCopy));
        }

        public async Task UpdateMailAsync(MailCopy mailCopy)
        {
            if (mailCopy == null)
            {
                _logger.Warning("Null mail passed to UpdateMailAsync call.");

                return;
            }

            _logger.Debug("Updating mail {MailCopyId} with Folder {FolderId}", mailCopy.Id, mailCopy.FolderId);

            await Connection.UpdateAsync(mailCopy).ConfigureAwait(false);

            ReportUIChange(new MailUpdatedMessage(mailCopy));
        }

        private async Task DeleteMailInternalAsync(MailCopy mailCopy)
        {
            if (mailCopy == null)
            {
                _logger.Warning("Null mail passed to DeleteMailAsync call.");

                return;
            }

            _logger.Debug("Deleting mail {Id} from folder {FolderName}", mailCopy.Id, mailCopy.AssignedFolder.FolderName);

            await Connection.DeleteAsync(mailCopy).ConfigureAwait(false);

            // If there are no more copies exists of the same mail, delete the MIME file as well.
            var isMailExists = await IsMailExistsAsync(mailCopy.Id).ConfigureAwait(false);

            if (!isMailExists)
            {
                await _mimeFileService.DeleteMimeMessageAsync(mailCopy.AssignedAccount.Id, mailCopy.FileId).ConfigureAwait(false);
            }

            ReportUIChange(new MailRemovedMessage(mailCopy));
        }

        #endregion

        private async Task UpdateAllMailCopiesAsync(string mailCopyId, Func<MailCopy, bool> action)
        {
            var mailCopies = await GetMailItemsAsync(mailCopyId);

            if (mailCopies == null || !mailCopies.Any())
            {
                _logger.Warning("Updating mail copies failed because there are no copies available with Id {MailCopyId}", mailCopyId);

                return;
            }

            _logger.Debug("Updating {MailCopyCount} mail copies with Id {MailCopyId}", mailCopies.Count, mailCopyId);

            foreach (var mailCopy in mailCopies)
            {
                bool shouldUpdateItem = action(mailCopy);

                if (shouldUpdateItem)
                {
                    await UpdateMailAsync(mailCopy).ConfigureAwait(false);
                }
                else
                    _logger.Debug("Skipped updating mail because it is already in the desired state.");
            }
        }

        public Task ChangeReadStatusAsync(string mailCopyId, bool isRead)
            => UpdateAllMailCopiesAsync(mailCopyId, (item) =>
            {
                if (item.IsRead == isRead) return false;

                item.IsRead = isRead;

                return true;
            });

        public Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged)
            => UpdateAllMailCopiesAsync(mailCopyId, (item) =>
            {
                if (item.IsFlagged == isFlagged) return false;

                item.IsFlagged = isFlagged;

                return true;
            });

        public async Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
        {
            // Note: Folder might not be available at the moment due to user not syncing folders before the delta processing.
            // This is a problem, because assignments won't be created.
            // Therefore we sync folders every time before the delta processing.

            var localFolder = await _folderService.GetFolderAsync(accountId, remoteFolderId);

            if (localFolder == null)
            {
                _logger.Warning("Local folder not found for remote folder {RemoteFolderId}", remoteFolderId);
                _logger.Warning("Skipping assignment creation for the the message {MailCopyId}", mailCopyId);

                return;
            }

            var mailCopy = await GetSingleMailItemWithoutFolderAssignmentAsync(mailCopyId);

            if (mailCopy == null)
            {
                _logger.Warning("Can't create assignment for mail {MailCopyId} because it does not exist.", mailCopyId);

                return;
            }

            // Copy one of the mail copy and assign it to the new folder.
            // We don't need to create a new MIME pack.
            // Therefore FileId is not changed for the new MailCopy.

            mailCopy.UniqueId = Guid.NewGuid();
            mailCopy.FolderId = localFolder.Id;
            mailCopy.AssignedFolder = localFolder;

            await InsertMailAsync(mailCopy).ConfigureAwait(false);
        }

        public async Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
        {
            var mailItem = await GetSingleMailItemAsync(mailCopyId, remoteFolderId).ConfigureAwait(false);

            if (mailItem == null)
            {
                _logger.Warning("Mail not found with id {MailCopyId} with remote folder {RemoteFolderId}", mailCopyId, remoteFolderId);

                return;
            }

            var localFolder = await _folderService.GetFolderAsync(accountId, remoteFolderId);

            if (localFolder == null)
            {
                _logger.Warning("Local folder not found for remote folder {RemoteFolderId}", remoteFolderId);

                return;
            }

            await DeleteMailInternalAsync(mailItem).ConfigureAwait(false);
        }

        public async Task<bool> CreateMailAsync(Guid accountId, NewMailItemPackage package)
        {
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

            if (account == null) return false;

            if (string.IsNullOrEmpty(package.AssignedRemoteFolderId))
            {
                _logger.Warning("Remote folder id is not set for {MailCopyId}.", package.Copy.Id);
                _logger.Warning("Ignoring creation of mail.");

                return false;
            }

            var assignedFolder = await _folderService.GetFolderAsync(accountId, package.AssignedRemoteFolderId).ConfigureAwait(false);

            if (assignedFolder == null)
            {
                _logger.Warning("Assigned folder not found for {MailCopyId}.", package.Copy.Id);
                _logger.Warning("Ignoring creation of mail.");

                return false;
            }

            var mailCopy = package.Copy;
            var mimeMessage = package.Mime;

            mailCopy.UniqueId = Guid.NewGuid();
            mailCopy.AssignedAccount = account;
            mailCopy.AssignedFolder = assignedFolder;
            mailCopy.SenderContact = await GetSenderContactForAccountAsync(account, mailCopy.FromAddress).ConfigureAwait(false);
            mailCopy.FolderId = assignedFolder.Id;

            // Only save MIME files if they don't exists.
            // This is because 1 mail may have multiple copies in different folders.
            // but only single MIME to represent all.

            // Save mime file to disk.
            var isMimeExists = await _mimeFileService.IsMimeExistAsync(accountId, mailCopy.FileId);

            if (!isMimeExists)
            {
                bool isMimeSaved = await _mimeFileService.SaveMimeMessageAsync(mailCopy.FileId, mimeMessage, accountId).ConfigureAwait(false);

                if (!isMimeSaved)
                {
                    _logger.Warning("Failed to save mime file for {MailCopyId}.", mailCopy.Id);
                }
            }

            // Save contact information.
            await _contactService.SaveAddressInformationAsync(mimeMessage).ConfigureAwait(false);

            // Create mail copy in the database.
            // Update if exists.

            var existingCopyItem = await Connection.Table<MailCopy>()
                                                    .FirstOrDefaultAsync(a => a.Id == mailCopy.Id && a.FolderId == assignedFolder.Id);

            if (existingCopyItem != null)
            {
                mailCopy.UniqueId = existingCopyItem.UniqueId;

                await UpdateMailAsync(mailCopy).ConfigureAwait(false);

                return false;
            }
            else
            {
                await InsertMailAsync(mailCopy).ConfigureAwait(false);

                return true;
            }
        }

        private async Task<MimeMessage> CreateDraftMimeAsync(MailAccount account, DraftCreationOptions draftCreationOptions)
        {
            // This unique id is stored in mime headers for Wino to identify remote message with local copy.
            // Same unique id will be used for the local copy as well.
            // Synchronizer will map this unique id to the local draft copy after synchronization.

            var message = new MimeMessage()
            {
                Headers = { { Constants.WinoLocalDraftHeader, Guid.NewGuid().ToString() } },
            };

            var primaryAlias = await _accountService.GetPrimaryAccountAliasAsync(account.Id) ?? throw new MissingAliasException();

            // Set FromName and FromAddress by alias.
            message.From.Add(new MailboxAddress(account.SenderName, primaryAlias.AliasAddress));

            var builder = new BodyBuilder();

            var signature = await GetSignature(account, draftCreationOptions.Reason);

            _ = draftCreationOptions.Reason switch
            {
                DraftCreationReason.Empty => CreateEmptyDraft(builder, message, draftCreationOptions, signature),
                _ => CreateReferencedDraft(builder, message, draftCreationOptions, account, signature),
            };

            builder.SetHtmlBody(builder.HtmlBody);

            message.Body = builder.ToMessageBody();

            return message;
        }

        private string CreateHtmlGap()
        {
            var template = $"""<div style="font-family: '{_preferencesService.ComposerFont}', Arial, sans-serif; font-size: {_preferencesService.ComposerFontSize}px"><br></div>""";
            return string.Concat(Enumerable.Repeat(template, 2));
        }

        private async Task<string> GetSignature(MailAccount account, DraftCreationReason reason)
        {
            if (account.Preferences.IsSignatureEnabled)
            {
                var signatureId = reason == DraftCreationReason.Empty ?
                    account.Preferences.SignatureIdForNewMessages :
                    account.Preferences.SignatureIdForFollowingMessages;

                if (signatureId != null)
                {
                    var signature = await _signatureService.GetSignatureAsync(signatureId.Value);

                    return signature.HtmlBody;
                }
            }

            return null;
        }

        private MimeMessage CreateEmptyDraft(BodyBuilder builder, MimeMessage message, DraftCreationOptions draftCreationOptions, string signature)
        {
            builder.HtmlBody = CreateHtmlGap();
            if (draftCreationOptions.MailToUri != null)
            {
                if (draftCreationOptions.MailToUri.Subject != null)
                    message.Subject = draftCreationOptions.MailToUri.Subject;

                if (draftCreationOptions.MailToUri.Body != null)
                {
                    builder.HtmlBody = $"""<div style="font-family: '{_preferencesService.ComposerFont}', Arial, sans-serif; font-size: {_preferencesService.ComposerFontSize}px">{draftCreationOptions.MailToUri.Body}</div>""" + builder.HtmlBody;
                }

                if (draftCreationOptions.MailToUri.To.Any())
                    message.To.AddRange(draftCreationOptions.MailToUri.To.Select(x => new MailboxAddress(x, x)));

                if (draftCreationOptions.MailToUri.Cc.Any())
                    message.Cc.AddRange(draftCreationOptions.MailToUri.Cc.Select(x => new MailboxAddress(x, x)));

                if (draftCreationOptions.MailToUri.Bcc.Any())
                    message.Bcc.AddRange(draftCreationOptions.MailToUri.Bcc.Select(x => new MailboxAddress(x, x)));
            }

            if (signature != null)
                builder.HtmlBody += signature;

            return message;
        }

        private MimeMessage CreateReferencedDraft(BodyBuilder builder, MimeMessage message, DraftCreationOptions draftCreationOptions, MailAccount account, string signature)
        {
            var reason = draftCreationOptions.Reason;
            var referenceMessage = draftCreationOptions.ReferencedMessage.MimeMessage;

            var gap = CreateHtmlGap();
            builder.HtmlBody = gap + CreateHtmlForReferencingMessage(referenceMessage);

            if (signature != null)
            {
                builder.HtmlBody = gap + signature + builder.HtmlBody;
            }

            // Manage "To"
            if (reason == DraftCreationReason.Reply || reason == DraftCreationReason.ReplyAll)
            {
                // Reply to the sender of the message
                if (referenceMessage.ReplyTo.Count > 0)
                    message.To.AddRange(referenceMessage.ReplyTo);
                else if (referenceMessage.From.Count > 0)
                    message.To.AddRange(referenceMessage.From);
                else if (referenceMessage.Sender != null)
                    message.To.Add(referenceMessage.Sender);

                if (reason == DraftCreationReason.ReplyAll)
                {
                    // Include all of the other original recipients
                    message.To.AddRange(referenceMessage.To.Where(x => x is MailboxAddress mailboxAddress && !mailboxAddress.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase)));
                    message.Cc.AddRange(referenceMessage.Cc.Where(x => x is MailboxAddress mailboxAddress && !mailboxAddress.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase)));
                }

                // Self email can be present at this step, when replying to own message. It should be removed only in case there no other recipients.
                if (message.To.Count > 1)
                {
                    var self = message.To.FirstOrDefault(x => x is MailboxAddress mailboxAddress && mailboxAddress.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase));
                    if (self != null)
                        message.To.Remove(self);
                }

                // Manage "ThreadId-ConversationId"
                if (!string.IsNullOrEmpty(referenceMessage.MessageId))
                {
                    message.InReplyTo = referenceMessage.MessageId;
                    message.References.AddRange(referenceMessage.References);
                    message.References.Add(referenceMessage.MessageId);
                }

                message.Headers.Add("Thread-Topic", referenceMessage.Subject);
            }

            // Manage Subject
            if (reason == DraftCreationReason.Forward && !referenceMessage.Subject.StartsWith("FW: ", StringComparison.OrdinalIgnoreCase))
                message.Subject = $"FW: {referenceMessage.Subject}";
            else if ((reason == DraftCreationReason.Reply || reason == DraftCreationReason.ReplyAll) && !referenceMessage.Subject.StartsWith("RE: ", StringComparison.OrdinalIgnoreCase))
                message.Subject = $"RE: {referenceMessage.Subject}";
            else if (referenceMessage != null)
                message.Subject = referenceMessage.Subject;

            // Only include attachments if forwarding.
            if (reason == DraftCreationReason.Forward && (referenceMessage?.Attachments?.Any() ?? false))
            {
                foreach (var attachment in referenceMessage.Attachments)
                {
                    builder.Attachments.Add(attachment);
                }
            }

            return message;

            // Generates html representation of To/Cc/From/Time and so on from referenced message.
            string CreateHtmlForReferencingMessage(MimeMessage referenceMessage)
            {
                var htmlMimeInfo = string.Empty;
                // Separation Line
                htmlMimeInfo += "<hr style='display:inline-block;width:100%' tabindex='-1'>";

                var visitor = _mimeFileService.CreateHTMLPreviewVisitor(referenceMessage, string.Empty);
                visitor.Visit(referenceMessage);

                htmlMimeInfo += $"""
                    <div id="divRplyFwdMsg" dir="ltr">
                      <font face="Calibri, sans-serif" style="font-size: 11pt;" color="#000000">
                        <b>From:</b> {ParticipantsToHtml(referenceMessage.From)}<br>
                        <b>Sent:</b> {referenceMessage.Date.ToLocalTime()}<br>
                        <b>To:</b> {ParticipantsToHtml(referenceMessage.To)}<br>
                        {(referenceMessage.Cc.Count > 0 ? $"<b>Cc:</b> {ParticipantsToHtml(referenceMessage.Cc)}<br>" : string.Empty)}
                        <b>Subject:</b> {referenceMessage.Subject}
                      </font>
                      <div>&nbsp;</div>
                      {visitor.HtmlBody}
                    </div>
                    """;

                return htmlMimeInfo;
            }

            static string ParticipantsToHtml(InternetAddressList internetAddresses) =>
                string.Join("; ", internetAddresses.Mailboxes
                                            .Select(x => $"{x.Name ?? Translator.UnknownSender} &lt;<a href=\"mailto:{x.Address ?? Translator.UnknownAddress}\">{x.Address ?? Translator.UnknownAddress}</a>&gt;"));
        }

        public async Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId)
        {
            var query = new Query("MailCopy")
                            .Join("MailItemFolder", "MailCopy.FolderId", "MailItemFolder.Id")
                            .Where("MailCopy.UniqueId", localDraftCopyUniqueId)
                            .Where("MailItemFolder.MailAccountId", accountId)
                            .SelectRaw("MailCopy.*")
                            .GetRawQuery();

            var localDraftCopy = await Connection.FindWithQueryAsync<MailCopy>(query);

            if (localDraftCopy == null)
            {
                _logger.Warning("Draft mapping failed because local draft copy with unique id {LocalDraftCopyUniqueId} does not exist.", localDraftCopyUniqueId);

                return false;
            }

            var oldLocalDraftId = localDraftCopy.Id;

            await LoadAssignedPropertiesAsync(localDraftCopy).ConfigureAwait(false);

            bool isIdChanging = localDraftCopy.Id != newMailCopyId;

            localDraftCopy.Id = newMailCopyId;
            localDraftCopy.DraftId = newDraftId;
            localDraftCopy.ThreadId = newThreadId;

            await UpdateMailAsync(localDraftCopy).ConfigureAwait(false);

            ReportUIChange(new DraftMapped(oldLocalDraftId, newDraftId));

            return true;
        }

        public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
        {
            return UpdateAllMailCopiesAsync(mailCopyId, (item) =>
            {
                if (item.ThreadId != newThreadId || item.DraftId != newDraftId)
                {
                    var oldDraftId = item.DraftId;

                    item.DraftId = newDraftId;
                    item.ThreadId = newThreadId;

                    ReportUIChange(new DraftMapped(oldDraftId, newDraftId));

                    return true;
                }

                return false;
            });
        }

        public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
        {
            var rawQuery = new Query("MailCopy")
                            .Join("MailItemFolder", "MailCopy.FolderId", "MailItemFolder.Id")
                            .WhereIn("MailCopy.Id", downloadedMailCopyIds)
                            .Where("MailCopy.IsRead", false)
                            .Where("MailItemFolder.MailAccountId", accountId)
                            .Where("MailItemFolder.SpecialFolderType", SpecialFolderType.Inbox)
                            .SelectRaw("MailCopy.*")
                            .GetRawQuery();

            return Connection.QueryAsync<MailCopy>(rawQuery);
        }

        public Task<MailAccount> GetMailAccountByUniqueIdAsync(Guid uniqueMailId)
        {
            var query = new Query("MailCopy")
                            .Join("MailItemFolder", "MailCopy.FolderId", "MailItemFolder.Id")
                            .Join("MailAccount", "MailItemFolder.MailAccountId", "MailAccount.Id")
                            .Where("MailCopy.UniqueId", uniqueMailId)
                            .SelectRaw("MailAccount.*")
                            .GetRawQuery();

            return Connection.FindWithQueryAsync<MailAccount>(query);
        }

        public Task<bool> IsMailExistsAsync(string mailCopyId)
            => Connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM MailCopy WHERE Id = ?)", mailCopyId);

        public Task<bool> IsMailExistsAsync(string mailCopyId, Guid folderId)
            => Connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM MailCopy WHERE Id = ? AND FolderId = ?)", mailCopyId, folderId);
    }
}
