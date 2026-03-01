using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Messaging.UI;
using Wino.Services.Extensions;

namespace Wino.Services;

public class MailService : BaseDatabaseService, IMailService
{
    private const int ItemLoadCount = 100;

    private readonly IFolderService _folderService;
    private readonly IContactService _contactService;
    private readonly IAccountService _accountService;
    private readonly ISignatureService _signatureService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IPreferencesService _preferencesService;

    private readonly ILogger _logger = Log.ForContext<MailService>();

    public MailService(IDatabaseService databaseService,
                       IFolderService folderService,
                       IContactService contactService,
                       IAccountService accountService,
                       ISignatureService signatureService,
                       IMimeFileService mimeFileService,
                       IPreferencesService preferencesService) : base(databaseService)
    {
        _folderService = folderService;
        _contactService = contactService;
        _accountService = accountService;
        _signatureService = signatureService;
        _mimeFileService = mimeFileService;
        _preferencesService = preferencesService;
    }

    public async Task<(MailCopy draftMailCopy, string draftBase64MimeMessage)> CreateDraftAsync(Guid accountId, DraftCreationOptions draftCreationOptions)
    {
        var composerAccount = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        var createdDraftMimeMessage = await CreateDraftMimeAsync(composerAccount, draftCreationOptions);

        var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(composerAccount.Id, SpecialFolderType.Draft);

        if (draftFolder == null)
            throw new UnavailableSpecialFolderException(SpecialFolderType.Draft, accountId);

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

        // If replying, add In-Reply-To, ThreadId and References per RFC 5322.
        // References must include all previous References + the Message-ID of the message being replied to.
        if (draftCreationOptions.ReferencedMessage != null)
        {
            var refMime = draftCreationOptions.ReferencedMessage.MimeMessage;
            var referenceMailCopy = draftCreationOptions.ReferencedMessage.MailCopy;

            string referenceMessageId = refMime?.MessageId;
            string referenceInReplyTo = refMime?.InReplyTo;
            IEnumerable<string> referenceChain = refMime?.References ?? [];

            // Fallback to MailCopy metadata if MIME lacks threading headers.
            if (string.IsNullOrWhiteSpace(referenceMessageId) && referenceMailCopy != null)
            {
                referenceMessageId = referenceMailCopy.MessageId;
                referenceInReplyTo = referenceMailCopy.InReplyTo;
                referenceChain = SplitStoredReferences(referenceMailCopy.References);
            }

            if (!string.IsNullOrWhiteSpace(referenceMessageId))
                copy.InReplyTo = MailHeaderExtensions.StripAngleBrackets(referenceMessageId);

            var refs = BuildReferencesChain(referenceChain, referenceInReplyTo, referenceMessageId);
            if (refs.Count > 0)
                copy.References = string.Join(";", refs);

            if (!string.IsNullOrEmpty(draftCreationOptions.ReferencedMessage.MailCopy?.ThreadId))
                copy.ThreadId = draftCreationOptions.ReferencedMessage.MailCopy.ThreadId;

            // Fallback local threading when provider/native thread id is unavailable.
            if (string.IsNullOrWhiteSpace(copy.ThreadId))
                copy.ThreadId = refs.FirstOrDefault() ?? copy.InReplyTo;
        }

        await Connection.InsertAsync(copy, typeof(MailCopy));

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

    public async Task<bool> HasAccountAnyDraftAsync(Guid accountId)
    {
        // Get the draft folder.
        var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Draft);

        if (draftFolder == null) return false;

        var draftCount = await Connection.Table<MailCopy>().Where(a => a.FolderId == draftFolder.Id).CountAsync();

        return draftCount > 0;
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

    public async Task<MailCopy> GetMailCopyByMessageIdAsync(Guid accountId, string messageId)
    {
        var normalizedMessageId = MailHeaderExtensions.StripAngleBrackets(messageId)?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMessageId))
            return null;

        var mailCopy = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy " +
            "INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id " +
            "WHERE MailItemFolder.MailAccountId = ? AND MailCopy.MessageId = ? " +
            "ORDER BY MailCopy.IsDraft ASC, MailCopy.CreationDate DESC LIMIT 1",
            accountId,
            normalizedMessageId).ConfigureAwait(false);

        if (mailCopy != null)
            await LoadAssignedPropertiesAsync(mailCopy).ConfigureAwait(false);

        return mailCopy;
    }

    private static (string Query, object[] Parameters) BuildMailFetchQuery(MailListInitializationOptions options)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id");

        var whereClauses = new List<string>();
        var parameters = new List<object>();

        // Folder filter
        var folderPlaceholders = string.Join(",", options.Folders.Select(_ => "?"));
        whereClauses.Add($"MailCopy.FolderId IN ({folderPlaceholders})");
        parameters.AddRange(options.Folders.Select(f => (object)f.Id));

        // Filter type
        switch (options.FilterType)
        {
            case FilterOptionType.Unread:
                whereClauses.Add("MailCopy.IsRead = 0");
                break;
            case FilterOptionType.Flagged:
                whereClauses.Add("MailCopy.IsFlagged = 1");
                break;
            case FilterOptionType.Files:
                whereClauses.Add("MailCopy.HasAttachments = 1");
                break;
        }

        // Focused filter
        if (options.IsFocusedOnly != null)
        {
            whereClauses.Add($"MailCopy.IsFocused = {(options.IsFocusedOnly.Value ? "1" : "0")}");
        }

        // Search query
        if (!string.IsNullOrEmpty(options.SearchQuery))
        {
            whereClauses.Add("(MailCopy.PreviewText LIKE ? OR MailCopy.Subject LIKE ? OR MailCopy.FromName LIKE ? OR MailCopy.FromAddress LIKE ?)");
            var searchPattern = $"%{options.SearchQuery}%";
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
        }

        // Exclude existing items
        if (options.ExistingUniqueIds?.Any() ?? false)
        {
            var excludePlaceholders = string.Join(",", options.ExistingUniqueIds.Select(_ => "?"));
            whereClauses.Add($"MailCopy.UniqueId NOT IN ({excludePlaceholders})");
            parameters.AddRange(options.ExistingUniqueIds.Keys.Select(id => (object)id));
        }

        if (whereClauses.Any())
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", whereClauses));
        }

        // Sorting
        if (options.SortingOptionType == SortingOptionType.ReceiveDate)
            sql.Append(" ORDER BY CreationDate DESC");
        else if (options.SortingOptionType == SortingOptionType.Sender)
            sql.Append(" ORDER BY FromName ASC");

        // Pagination
        var limit = options.Take > 0 ? options.Take : ItemLoadCount;
        sql.Append($" LIMIT {limit}");

        if (options.Skip > 0)
        {
            sql.Append($" OFFSET {options.Skip}");
        }

        return (sql.ToString(), parameters.ToArray());
    }

    private static List<MailCopy> ApplyOptionsToPreFetchedMails(MailListInitializationOptions options)
    {
        var allowedFolderIds = options.Folders.Select(f => f.Id).ToHashSet();

        IEnumerable<MailCopy> query = options.PreFetchMailCopies
            .Where(m => m != null && allowedFolderIds.Contains(m.FolderId))
            .GroupBy(m => m.UniqueId)
            .Select(g => g.First());

        switch (options.FilterType)
        {
            case FilterOptionType.Unread:
                query = query.Where(m => !m.IsRead);
                break;
            case FilterOptionType.Flagged:
                query = query.Where(m => m.IsFlagged);
                break;
            case FilterOptionType.Files:
                query = query.Where(m => m.HasAttachments);
                break;
        }

        if (options.IsFocusedOnly is bool isFocused)
        {
            query = query.Where(m => m.IsFocused == isFocused);
        }

        if (!string.IsNullOrWhiteSpace(options.SearchQuery))
        {
            var search = options.SearchQuery.Trim();
            query = query.Where(m =>
                (!string.IsNullOrEmpty(m.PreviewText) && m.PreviewText.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(m.Subject) && m.Subject.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(m.FromName) && m.FromName.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(m.FromAddress) && m.FromAddress.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (options.ExistingUniqueIds?.Any() ?? false)
        {
            query = query.Where(m => !options.ExistingUniqueIds.ContainsKey(m.UniqueId));
        }

        query = options.SortingOptionType switch
        {
            SortingOptionType.Sender => query.OrderBy(m => m.FromName).ThenByDescending(m => m.CreationDate),
            _ => query.OrderByDescending(m => m.CreationDate)
        };

        if (options.Skip > 0)
        {
            query = query.Skip(options.Skip);
        }

        if (options.Take > 0)
        {
            query = query.Take(options.Take);
        }

        return query.ToList();
    }

    public async Task<List<MailCopy>> FetchMailsAsync(MailListInitializationOptions options, CancellationToken cancellationToken = default)
    {
        List<MailCopy> mails;

        if (options.PreFetchMailCopies != null)
        {
            mails = ApplyOptionsToPreFetchedMails(options);
        }
        else
        {
            var (query, parameters) = BuildMailFetchQuery(options);
            mails = await Connection.QueryAsync<MailCopy>(query, parameters);
        }

        if (mails.Count == 0)
            return mails;

        cancellationToken.ThrowIfCancellationRequested();

        // Pre-load all data needed for property assignment in as few DB round-trips as possible.
        // 1. Seed the folder cache directly from the options folders - these cover the vast majority
        //    of mails in a normal folder view and require zero extra DB calls.
        var folderCache = options.Folders
            .OfType<MailItemFolder>()
            .ToDictionary(f => f.Id);

        // 2. Load all accounts in one call (typically 1-5 accounts) instead of N per-mail lookups.
        var allAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountCache = allAccounts.ToDictionary(a => a.Id);

        // 3. Fetch any folders not already in the cache (rare for normal views, common for merged inboxes
        //    that include Sent/Draft copies belonging to different folder objects).
        var uncachedFolderIds = mails
            .Select(m => m.FolderId)
            .Distinct()
            .Where(id => !folderCache.ContainsKey(id))
            .ToList();

        if (uncachedFolderIds.Count > 0)
        {
            var folders = await Task.WhenAll(
                uncachedFolderIds.Select(id => _folderService.GetFolderAsync(id))).ConfigureAwait(false);

            foreach (var f in folders.Where(f => f != null))
                folderCache[f.Id] = f;
        }

        // 4. Batch-fetch all sender contacts in a single SQL IN(...) query instead of one query per mail.
        var uniqueAddresses = mails
            .Where(m => !string.IsNullOrEmpty(m.FromAddress))
            .Select(m => m.FromAddress)
            .Distinct()
            .ToList();

        var contactList = await _contactService.GetContactsByAddressesAsync(uniqueAddresses).ConfigureAwait(false);
        var contactCache = contactList.ToDictionary(c => c.Address);

        cancellationToken.ThrowIfCancellationRequested();

        // 5. Assign all properties synchronously from the pre-loaded in-memory caches - no DB calls here.
        AssignPropertiesFromCaches(mails, folderCache, accountCache, contactCache);
        mails.RemoveAll(m => m.AssignedAccount == null || m.AssignedFolder == null);

        if (!options.CreateThreads || mails.Count == 0)
            return [.. mails];

        // 6. Expand threads: one batch query for all sibling mails across all threads.
        var uniqueThreadIds = mails
            .Where(m => !string.IsNullOrEmpty(m.ThreadId))
            .Select(m => m.ThreadId)
            .Distinct()
            .ToList();

        if (uniqueThreadIds.Count == 0)
            return [.. mails];

        var existingMailIds = mails.Select(m => m.Id).ToHashSet();
        var threadMails = await GetMailsByThreadIdsAsync(uniqueThreadIds, existingMailIds).ConfigureAwait(false);

        if (threadMails?.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Load any folders that thread mails belong to but are not yet cached.
            var newFolderIds = threadMails
                .Select(m => m.FolderId)
                .Distinct()
                .Where(id => !folderCache.ContainsKey(id))
                .ToList();

            if (newFolderIds.Count > 0)
            {
                var newFolders = await Task.WhenAll(
                    newFolderIds.Select(id => _folderService.GetFolderAsync(id))).ConfigureAwait(false);

                foreach (var f in newFolders.Where(f => f != null))
                    folderCache[f.Id] = f;
            }

            // Batch-fetch contacts for any new senders in thread mails.
            var newAddresses = threadMails
                .Where(m => !string.IsNullOrEmpty(m.FromAddress) && !contactCache.ContainsKey(m.FromAddress))
                .Select(m => m.FromAddress)
                .Distinct()
                .ToList();

            if (newAddresses.Count > 0)
            {
                var newContacts = await _contactService.GetContactsByAddressesAsync(newAddresses).ConfigureAwait(false);
                foreach (var c in newContacts.Where(c => c != null))
                    contactCache[c.Address] = c;
            }

            AssignPropertiesFromCaches(threadMails, folderCache, accountCache, contactCache);
            mails.AddRange(threadMails.Where(m => m.AssignedAccount != null && m.AssignedFolder != null));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [.. mails];
    }

    /// <summary>
    /// Assigns AssignedFolder, AssignedAccount, and SenderContact to each mail from pre-loaded
    /// in-memory dictionaries. No DB calls are made here.
    /// </summary>
    private void AssignPropertiesFromCaches(
        List<MailCopy> mails,
        Dictionary<Guid, MailItemFolder> folderCache,
        Dictionary<Guid, MailAccount> accountCache,
        Dictionary<string, AccountContact> contactCache)
    {
        foreach (var mail in mails)
        {
            if (!folderCache.TryGetValue(mail.FolderId, out var folder))
                continue;

            if (!accountCache.TryGetValue(folder.MailAccountId, out var account))
                continue;

            mail.AssignedFolder = folder;
            mail.AssignedAccount = account;

            // Self-sent mails (e.g. Sent folder): construct contact from account meta
            // to get the up-to-date profile picture without a DB roundtrip.
            if (!string.IsNullOrEmpty(mail.FromAddress) && mail.FromAddress == account.Address)
            {
                mail.SenderContact = new AccountContact
                {
                    Address = account.Address,
                    Name = account.SenderName,
                    Base64ContactPicture = account.Base64ProfilePictureData
                };
            }
            else
            {
                contactCache.TryGetValue(mail.FromAddress ?? string.Empty, out var contact);
                mail.SenderContact = contact ?? CreateUnknownContact(mail.FromName, mail.FromAddress);
            }
        }
    }

    private async Task<List<MailCopy>> GetMailsByThreadIdsAsync(List<string> threadIds, HashSet<string> excludeMailIds)
    {
        if (threadIds?.Count == 0)
            return [];

        var threadPlaceholders = string.Join(",", threadIds.Select(_ => "?"));
        var parameters = new List<object>();
        parameters.AddRange(threadIds.Cast<object>());

        string sql;
        if (excludeMailIds.Count > 0)
        {
            var excludePlaceholders = string.Join(",", excludeMailIds.Select(_ => "?"));
            sql = $"SELECT MailCopy.* FROM MailCopy WHERE ThreadId IN ({threadPlaceholders}) AND Id NOT IN ({excludePlaceholders})";
            parameters.AddRange(excludeMailIds.Cast<object>());
        }
        else
        {
            sql = $"SELECT MailCopy.* FROM MailCopy WHERE ThreadId IN ({threadPlaceholders})";
        }

        return await Connection.QueryAsync<MailCopy>(sql, parameters.ToArray()).ConfigureAwait(false);
    }

    private static AccountContact CreateUnknownContact(string fromName, string fromAddress)
    {
        if (string.IsNullOrEmpty(fromName) && string.IsNullOrEmpty(fromAddress))
        {
            return new AccountContact()
            {
                Name = Translator.UnknownSender,
                Address = Translator.UnknownAddress
            };
        }
        else
        {
            if (string.IsNullOrEmpty(fromName)) fromName = fromAddress;

            return new AccountContact()
            {
                Name = fromName,
                Address = fromAddress
            };
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

    /// <summary>
    /// Using this override is dangerous.
    /// Gmail stores multiple copies of same mail in different folders.
    /// This one will always return the first one. Use with caution.
    /// </summary>
    /// <param name="mailCopyId">Mail copy id.</param>
    public async Task<MailCopy> GetSingleMailItemAsync(string mailCopyId)
    {
        var mailCopy = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy WHERE MailCopy.Id = ?",
            mailCopyId);
        if (mailCopy == null) return null;

        await LoadAssignedPropertiesAsync(mailCopy).ConfigureAwait(false);

        return mailCopy;
    }

    public async Task<MailCopy> GetSingleMailItemAsync(string mailCopyId, string remoteFolderId)
    {
        var mailItem = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.Id = ? AND MailItemFolder.RemoteFolderId = ?",
            mailCopyId, remoteFolderId);

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
            // Delete mime file as well.
            // Even though Gmail might have multiple copies for the same mail, we only have one MIME file for all.
            // Their FileId is inserted same.

            await DeleteMailInternalAsync(mailItem, preserveMimeFile: false).ConfigureAwait(false);
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

        await Connection.InsertAsync(mailCopy, typeof(MailCopy)).ConfigureAwait(false);

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

        await Connection.UpdateAsync(mailCopy, typeof(MailCopy)).ConfigureAwait(false);

        ReportUIChange(new MailUpdatedMessage(mailCopy, MailUpdateSource.Server));
    }

    private async Task DeleteMailInternalAsync(MailCopy mailCopy, bool preserveMimeFile)
    {
        if (mailCopy == null)
        {
            _logger.Warning("Null mail passed to DeleteMailAsync call.");

            return;
        }

        _logger.Debug("Deleting mail {Id} from folder {FolderName}", mailCopy.Id, mailCopy.AssignedFolder.FolderName);

        await Connection.DeleteAsync<MailCopy>(mailCopy.UniqueId).ConfigureAwait(false);

        // If there are no more copies exists of the same mail, delete the MIME file as well.
        var isMailExists = await IsMailExistsAsync(mailCopy.Id).ConfigureAwait(false);

        if (!isMailExists && !preserveMimeFile)
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
            if (isRead && item.UniqueId != Guid.Empty)
            {
                WeakReferenceMessenger.Default.Send(new MailReadStatusChanged(item.UniqueId));
            }

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

        if (mailCopy.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent &&
            localFolder.SpecialFolderType == SpecialFolderType.Deleted)
        {
            // Sent item is deleted.
            // Gmail does not delete the sent items, but moves them to the deleted folder.
            // API doesn't allow removing Sent label.
            // Here we intercept this behavior, removing the Sent copy of the mail and adding the Deleted copy.
            // This way item will only be visible in Trash folder as in Gmail Web UI.
            // Don't delete MIME file since if exists.

            await DeleteMailInternalAsync(mailCopy, preserveMimeFile: true).ConfigureAwait(false);
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

        await DeleteMailInternalAsync(mailItem, preserveMimeFile: false).ConfigureAwait(false);
    }

    public async Task CreateMailRawAsync(MailAccount account, MailItemFolder mailItemFolder, NewMailItemPackage package)
    {
        var mailCopy = package.Copy;
        var mimeMessage = package.Mime;

        mailCopy.UniqueId = Guid.NewGuid();
        mailCopy.AssignedAccount = account;
        mailCopy.AssignedFolder = mailItemFolder;
        mailCopy.SenderContact = await GetSenderContactForAccountAsync(account, mailCopy.FromAddress).ConfigureAwait(false);
        mailCopy.FolderId = mailItemFolder.Id;

        await SaveContactsForPackageAsync(package).ConfigureAwait(false);

        var mimeSaveTask = _mimeFileService.SaveMimeMessageAsync(mailCopy.FileId, mimeMessage, account.Id);
        var insertMailTask = InsertMailAsync(mailCopy);

        await Task.WhenAll(mimeSaveTask, insertMailTask).ConfigureAwait(false);
    }

    public async Task CreateMailAsyncEx(Guid accountId, NewMailItemPackage package)
    {

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

        // Save mime file to disk if provided.

        if (mimeMessage != null)
        {
            var isMimeExists = await _mimeFileService.IsMimeExistAsync(accountId, mailCopy.FileId).ConfigureAwait(false);

            if (!isMimeExists)
            {
                bool isMimeSaved = await _mimeFileService.SaveMimeMessageAsync(mailCopy.FileId, mimeMessage, accountId).ConfigureAwait(false);

                if (!isMimeSaved)
                {
                    _logger.Warning("Failed to save mime file for {MailCopyId}.", mailCopy.Id);
                }
            }

        }

        // Save contact information extracted from provider API or MIME before insert/update.
        await SaveContactsForPackageAsync(package).ConfigureAwait(false);

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
            if (account.ProviderType != MailProviderType.Gmail)
            {
                // Make sure there is only 1 instance left of this mail copy id.
                var allMails = await GetMailItemsAsync(mailCopy.Id).ConfigureAwait(false);

                await DeleteMailAsync(accountId, mailCopy.Id).ConfigureAwait(false);
            }

            await InsertMailAsync(mailCopy).ConfigureAwait(false);

            return true;
        }
    }

    private async Task SaveContactsForPackageAsync(NewMailItemPackage package)
    {
        if (package == null) return;

        if (package.Mime != null)
        {
            await _contactService.SaveAddressInformationAsync(package.Mime).ConfigureAwait(false);
            return;
        }

        var contacts = package.ExtractedContacts?
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Address))
            .ToList() ?? new List<AccountContact>();

        var senderAddress = package.Copy?.FromAddress;
        if (!string.IsNullOrWhiteSpace(senderAddress))
        {
            contacts.Add(new AccountContact
            {
                Address = senderAddress,
                Name = string.IsNullOrWhiteSpace(package.Copy?.FromName) ? senderAddress : package.Copy.FromName
            });
        }

        if (contacts.Count == 0) return;

        await _contactService.SaveAddressInformationAsync(contacts).ConfigureAwait(false);
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
        var ownAddresses = await GetOwnAddressesAsync(account).ConfigureAwait(false);

        _ = draftCreationOptions.Reason switch
        {
            DraftCreationReason.Empty => CreateEmptyDraft(builder, message, draftCreationOptions, signature),
            _ => CreateReferencedDraft(builder, message, draftCreationOptions, signature, ownAddresses),
        };

        // TODO: Migration
        // builder.SetHtmlBody(builder.HtmlBody);

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
                // TODO: In .NET 6+ replace with string "ReplaceLineEndings" method.
                var escapedBody = draftCreationOptions.MailToUri.Body.Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
                builder.HtmlBody = $"""<div style="font-family: '{_preferencesService.ComposerFont}', Arial, sans-serif; font-size: {_preferencesService.ComposerFontSize}px">{escapedBody}</div>""" + builder.HtmlBody;
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

    private MimeMessage CreateReferencedDraft(BodyBuilder builder,
                                              MimeMessage message,
                                              DraftCreationOptions draftCreationOptions,
                                              string signature,
                                              ISet<string> ownAddresses)
    {
        var reason = draftCreationOptions.Reason;
        var referenceMessage = draftCreationOptions.ReferencedMessage.MimeMessage;
        ownAddresses ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var gap = CreateHtmlGap();
        builder.HtmlBody = gap + CreateHtmlForReferencingMessage(referenceMessage);

        if (signature != null)
        {
            builder.HtmlBody = gap + signature + builder.HtmlBody;
        }

        // Manage "To"
        if (reason == DraftCreationReason.Reply || reason == DraftCreationReason.ReplyAll)
        {
            var toRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ccRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddToRecipient(MailboxAddress mailbox, bool allowSelf)
            {
                var address = mailbox?.Address?.Trim();
                if (string.IsNullOrWhiteSpace(address))
                    return;
                if (!allowSelf && ownAddresses.Contains(address))
                    return;
                if (!toRecipients.Add(address))
                    return;

                message.To.Add(new MailboxAddress(mailbox.Name, address));
            }

            void AddCcRecipient(MailboxAddress mailbox, bool allowSelf)
            {
                var address = mailbox?.Address?.Trim();
                if (string.IsNullOrWhiteSpace(address))
                    return;
                if (!allowSelf && ownAddresses.Contains(address))
                    return;
                if (toRecipients.Contains(address) || !ccRecipients.Add(address))
                    return;

                message.Cc.Add(new MailboxAddress(mailbox.Name, address));
            }

            // Reply target follows Reply-To first, then From, then Sender.
            if (referenceMessage.ReplyTo.Mailboxes.Any())
            {
                foreach (var mailbox in referenceMessage.ReplyTo.Mailboxes)
                    AddToRecipient(mailbox, allowSelf: true);
            }
            else if (referenceMessage.From.Mailboxes.Any())
            {
                foreach (var mailbox in referenceMessage.From.Mailboxes)
                    AddToRecipient(mailbox, allowSelf: true);
            }
            else if (referenceMessage.Sender is MailboxAddress senderMailbox)
            {
                AddToRecipient(senderMailbox, allowSelf: true);
            }

            if (reason == DraftCreationReason.ReplyAll)
            {
                // Include all of the other original recipients
                foreach (var mailbox in referenceMessage.To.Mailboxes)
                    AddToRecipient(mailbox, allowSelf: false);

                foreach (var mailbox in referenceMessage.Cc.Mailboxes)
                    AddCcRecipient(mailbox, allowSelf: false);
            }

            // Self email can be present at this step, when replying to own message. It should be removed only in case there no other recipients.
            if (message.To.Mailboxes.Count() > 1)
            {
                var selfRecipients = message.To.Mailboxes
                    .Where(m => ownAddresses.Contains(m.Address ?? string.Empty))
                    .ToList();

                foreach (var self in selfRecipients)
                {
                    message.To.Remove(self);
                }
            }

            // Manage "ThreadId-ConversationId"
            // CRITICAL: In-Reply-To and References headers are essential for threading
            // They must reference the original message's Message-ID from the MIME headers
            if (!string.IsNullOrEmpty(referenceMessage.MessageId))
            {
                message.InReplyTo = MailHeaderExtensions.StripAngleBrackets(referenceMessage.MessageId);

                var refs = BuildReferencesChain(
                    referenceMessage.References,
                    referenceMessage.InReplyTo,
                    referenceMessage.MessageId);

                foreach (var referenceId in refs)
                    message.References.Add(referenceId);
            }
            else
            {
                // WARNING: Reference message has no Message-ID!
                // This will break threading. Try to use the MessageId from MailCopy if available.
                var referenceMailCopy = draftCreationOptions.ReferencedMessage.MailCopy;
                if (referenceMailCopy != null && !string.IsNullOrEmpty(referenceMailCopy.MessageId))
                {
                    message.InReplyTo = MailHeaderExtensions.StripAngleBrackets(referenceMailCopy.MessageId);

                    var refs = BuildReferencesChain(
                        SplitStoredReferences(referenceMailCopy.References),
                        referenceMailCopy.InReplyTo,
                        referenceMailCopy.MessageId);

                    foreach (var referenceId in refs)
                        message.References.Add(referenceId);
                }
            }

            if (!string.IsNullOrEmpty(referenceMessage.Subject))
                message.Headers.Add("Thread-Topic", referenceMessage.Subject);
        }

        // Manage Subject
        var referenceSubject = referenceMessage?.Subject ?? string.Empty;
        if (reason == DraftCreationReason.Forward && !referenceSubject.StartsWith("FW: ", StringComparison.OrdinalIgnoreCase))
            message.Subject = $"FW: {referenceSubject}";
        else if ((reason == DraftCreationReason.Reply || reason == DraftCreationReason.ReplyAll) && !referenceSubject.StartsWith("RE: ", StringComparison.OrdinalIgnoreCase))
            message.Subject = $"RE: {referenceSubject}";
        else
            message.Subject = referenceSubject;

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
        var localDraftCopy = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.UniqueId = ? AND MailItemFolder.MailAccountId = ?",
            localDraftCopyUniqueId, accountId);

        if (localDraftCopy == null)
        {
            _logger.Warning("Draft mapping failed because local draft copy with unique id {LocalDraftCopyUniqueId} does not exist.", localDraftCopyUniqueId);

            return false;
        }

        var oldLocalDraftId = localDraftCopy.Id;

        await LoadAssignedPropertiesAsync(localDraftCopy).ConfigureAwait(false);

        bool isIdChanging = localDraftCopy.Id != newMailCopyId;

        localDraftCopy.Id = newMailCopyId;
        if (!string.IsNullOrEmpty(newDraftId))
            localDraftCopy.DraftId = newDraftId;
        if (!string.IsNullOrEmpty(newThreadId))
            localDraftCopy.ThreadId = newThreadId;

        await UpdateMailAsync(localDraftCopy).ConfigureAwait(false);

        ReportUIChange(new DraftMapped(oldLocalDraftId, localDraftCopy.DraftId));

        return true;
    }

    public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
    {
        return UpdateAllMailCopiesAsync(mailCopyId, (item) =>
        {
            var shouldUpdateThreadId = !string.IsNullOrEmpty(newThreadId);
            var shouldUpdateDraftId = !string.IsNullOrEmpty(newDraftId);

            if ((shouldUpdateThreadId && item.ThreadId != newThreadId) ||
                (shouldUpdateDraftId && item.DraftId != newDraftId))
            {
                var oldDraftId = item.DraftId;

                if (shouldUpdateDraftId)
                    item.DraftId = newDraftId;
                if (shouldUpdateThreadId)
                    item.ThreadId = newThreadId;

                ReportUIChange(new DraftMapped(oldDraftId, item.DraftId));

                return true;
            }

            return false;
        });
    }

    public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
    {
        var placeholders = string.Join(",", downloadedMailCopyIds.Select(_ => "?"));
        var sql = $"SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.Id IN ({placeholders}) AND MailCopy.IsRead = ? AND MailItemFolder.MailAccountId = ? AND MailItemFolder.SpecialFolderType = ?";
        var parameters = new List<object>();
        parameters.AddRange(downloadedMailCopyIds.Cast<object>());
        parameters.Add(false);
        parameters.Add(accountId);
        parameters.Add((int)SpecialFolderType.Inbox);

        return Connection.QueryAsync<MailCopy>(sql, parameters.ToArray());
    }

    public Task<MailAccount> GetMailAccountByUniqueIdAsync(Guid uniqueMailId)
    {
        return Connection.FindWithQueryAsync<MailAccount>(
            "SELECT MailAccount.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id INNER JOIN MailAccount ON MailItemFolder.MailAccountId = MailAccount.Id WHERE MailCopy.UniqueId = ?",
            uniqueMailId);
    }

    public Task<bool> IsMailExistsAsync(string mailCopyId)
        => Connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM MailCopy WHERE Id = ?)", mailCopyId);

    public async Task<List<MailCopy>> GetExistingMailsAsync(Guid folderId, IEnumerable<MailKit.UniqueId> uniqueIds)
    {
        var localMailIds = uniqueIds.Select(a => MailkitClientExtensions.CreateUid(folderId, a.Id)).ToArray();

        var placeholders = string.Join(",", localMailIds.Select(_ => "?"));
        var sql = $"SELECT * FROM MailCopy WHERE Id IN ({placeholders})";

        return await Connection.QueryAsync<MailCopy>(sql, localMailIds.Cast<object>().ToArray());
    }

    public Task<bool> IsMailExistsAsync(string mailCopyId, Guid folderId)
        => Connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM MailCopy WHERE Id = ? AND FolderId = ?)", mailCopyId, folderId);

    public async Task<GmailArchiveComparisonResult> GetGmailArchiveComparisonResultAsync(Guid archiveFolderId, List<string> onlineArchiveMailIds)
    {
        onlineArchiveMailIds ??= [];

        var localArchiveMails = await Connection.Table<MailCopy>()
                                            .Where(a => a.FolderId == archiveFolderId)
                                            .ToListAsync().ConfigureAwait(false);

        var onlineArchiveIdSet = onlineArchiveMailIds
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.Ordinal);

        var localArchiveIdSet = localArchiveMails
            .Select(a => a.Id)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.Ordinal);

        var removedMails = localArchiveIdSet.Except(onlineArchiveIdSet).ToArray();
        var addedMails = onlineArchiveIdSet.Except(localArchiveIdSet).ToArray();

        return new GmailArchiveComparisonResult(addedMails, removedMails);
    }

    private async Task<HashSet<string>> GetOwnAddressesAsync(MailAccount account)
    {
        var ownAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(account?.Address))
            ownAddresses.Add(account.Address.Trim());

        var aliases = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias?.AliasAddress))
                    ownAddresses.Add(alias.AliasAddress.Trim());
            }
        }

        return ownAddresses;
    }

    private static IEnumerable<string> SplitStoredReferences(string references)
    {
        if (string.IsNullOrWhiteSpace(references))
            return [];

        return references
            .Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim());
    }

    private static List<string> BuildReferencesChain(IEnumerable<string> existingReferences, string parentInReplyTo, string parentMessageId)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(string value)
        {
            var normalized = MailHeaderExtensions.StripAngleBrackets(value)?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            if (!seen.Add(normalized))
                return;

            results.Add(normalized);
        }

        if (existingReferences != null)
        {
            foreach (var reference in existingReferences)
                AddReference(reference);
        }

        // RFC 5322 fallback: if References is absent, include parent In-Reply-To first when available.
        if (results.Count == 0)
            AddReference(parentInReplyTo);

        AddReference(parentMessageId);

        return results;
    }

    public async Task<IEnumerable<string>> GetRecentMailIdsForFolderAsync(Guid folderId, int count)
    {
        var recentMails = await Connection.Table<MailCopy>()
            .Where(a => a.FolderId == folderId)
            .OrderByDescending(a => a.CreationDate)
            .Take(count)
            .ToListAsync()
            .ConfigureAwait(false);

        return recentMails.Select(m => m.Id);
    }

    public async Task<List<MailCopy>> GetMailItemsAsync(IEnumerable<string> mailCopyIds)
    {
        if (!mailCopyIds.Any()) return [];

        var placeholders = string.Join(",", mailCopyIds.Select(_ => "?"));
        var sql = $"SELECT MailCopy.* FROM MailCopy WHERE MailCopy.Id IN ({placeholders})";

        var mailCopies = await Connection.QueryAsync<MailCopy>(sql, mailCopyIds.Cast<object>().ToArray());
        if (mailCopies?.Count == 0) return [];

        var folderIds = mailCopies.Select(m => m.FolderId).Distinct().ToList();
        var folderTasks = folderIds.Select(id => _folderService.GetFolderAsync(id));
        var folders = await Task.WhenAll(folderTasks).ConfigureAwait(false);
        var folderCache = folders.Where(f => f != null).ToDictionary(f => f.Id);

        var allAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountCache = allAccounts.ToDictionary(a => a.Id);

        var addresses = mailCopies.Where(m => !string.IsNullOrEmpty(m.FromAddress)).Select(m => m.FromAddress).Distinct().ToList();
        var contactList = await _contactService.GetContactsByAddressesAsync(addresses).ConfigureAwait(false);
        var contactCache = contactList.ToDictionary(c => c.Address);

        AssignPropertiesFromCaches(mailCopies, folderCache, accountCache, contactCache);

        return mailCopies;
    }

    public async Task<List<string>> AreMailsExistsAsync(IEnumerable<string> mailCopyIds)
    {
        var placeholders = string.Join(",", mailCopyIds.Select(_ => "?"));
        var sql = $"SELECT Id FROM MailCopy WHERE Id IN ({placeholders})";

        return await Connection.QueryScalarsAsync<string>(sql, mailCopyIds.Cast<object>().ToArray());
    }

    public Task<List<MailCopy>> GetMailCopiesBeforeDateAsync(Guid accountId, DateTime cutoffDateUtc)
    {
        const string query = """
                             SELECT MailCopy.*
                             FROM MailCopy
                             INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id
                             WHERE MailItemFolder.MailAccountId = ?
                               AND MailCopy.CreationDate < ?
                             """;

        return Connection.QueryAsync<MailCopy>(query, accountId, cutoffDateUtc);
    }
}
