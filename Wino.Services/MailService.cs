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
using Wino.Core.Domain.Misc;
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
    private readonly ISentMailReceiptService _sentMailReceiptService;
    private readonly IMailCategoryService _mailCategoryService;

    private readonly ILogger _logger = Log.ForContext<MailService>();

    public MailService(IDatabaseService databaseService,
                       IFolderService folderService,
                       IContactService contactService,
                       IAccountService accountService,
                       ISignatureService signatureService,
                       IMimeFileService mimeFileService,
                       IPreferencesService preferencesService,
                       ISentMailReceiptService sentMailReceiptService,
                       IMailCategoryService mailCategoryService) : base(databaseService)
    {
        _folderService = folderService;
        _contactService = contactService;
        _accountService = accountService;
        _signatureService = signatureService;
        _mimeFileService = mimeFileService;
        _preferencesService = preferencesService;
        _sentMailReceiptService = sentMailReceiptService;
        _mailCategoryService = mailCategoryService;
    }

    public async Task<(MailCopy draftMailCopy, string draftBase64MimeMessage)> CreateDraftAsync(Guid accountId, DraftCreationOptions draftCreationOptions)
    {
        var composerAccount = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        var selectedAlias = await ResolveDraftAliasAsync(composerAccount, draftCreationOptions).ConfigureAwait(false);
        var createdDraftMimeMessage = await CreateDraftMimeAsync(composerAccount, draftCreationOptions, selectedAlias).ConfigureAwait(false);

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
            FromAddress = selectedAlias?.AliasAddress ?? primaryAlias?.AliasAddress ?? composerAccount.Address,
            FromName = selectedAlias?.AliasSenderName ?? composerAccount.SenderName,
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
            FileId = Guid.NewGuid(),
            MessageId = GetNormalizedMimeMessageId(createdDraftMimeMessage),
            InReplyTo = GetNormalizedMimeInReplyTo(createdDraftMimeMessage),
            References = GetNormalizedMimeReferences(createdDraftMimeMessage)
        };

        if (draftCreationOptions.ReferencedMessage != null)
        {
            if (!string.IsNullOrEmpty(draftCreationOptions.ReferencedMessage.MailCopy?.ThreadId))
                copy.ThreadId = draftCreationOptions.ReferencedMessage.MailCopy.ThreadId;

            // Fallback local threading when provider/native thread id is unavailable.
            if (string.IsNullOrWhiteSpace(copy.ThreadId))
                copy.ThreadId = MailHeaderExtensions.SplitMessageIds(copy.References).FirstOrDefault() ?? copy.InReplyTo;
        }

        await Connection.InsertAsync(copy, typeof(MailCopy));

        await _mimeFileService.SaveMimeMessageAsync(copy.FileId, createdDraftMimeMessage, composerAccount.Id);

        ReportUIChange(new DraftCreated(copy, composerAccount));

        return (copy, createdDraftMimeMessage.GetBase64MimeMessage());
    }

    public async Task<List<MailCopy>> GetMailsByFolderIdAsync(Guid folderId)
    {
        var mails = await Connection.QueryAsync<MailCopy>("SELECT * FROM MailCopy WHERE FolderId = ?", folderId).ConfigureAwait(false);

        return await HydrateMailCopiesAsync(mails).ConfigureAwait(false);
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
        var unreadMails = await Connection.QueryAsync<MailCopy>("SELECT * FROM MailCopy WHERE FolderId = ? AND IsRead = 0", folderId).ConfigureAwait(false);

        return await HydrateMailCopiesAsync(unreadMails).ConfigureAwait(false);
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

        return await HydrateMailCopyAsync(mailCopy).ConfigureAwait(false);
    }

    private static (string Query, object[] Parameters) BuildMailFetchQuery(MailListInitializationOptions options, bool pinnedOnly = false)
    {
        var sql = new StringBuilder();
        sql.Append(options.IsCategoryView
            ? "SELECT DISTINCT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id INNER JOIN MailCategoryAssignment ON MailCopy.UniqueId = MailCategoryAssignment.MailCopyUniqueId"
            : "SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id");

        var whereClauses = new List<string>();
        var parameters = new List<object>();

        // Folder filter
        var folderPlaceholders = string.Join(",", options.Folders.Select(_ => "?"));
        whereClauses.Add($"MailCopy.FolderId IN ({folderPlaceholders})");
        parameters.AddRange(options.Folders.Select(f => (object)f.Id));

        if (options.IsCategoryView)
        {
            var categoryPlaceholders = string.Join(",", options.CategoryIds.Select(_ => "?"));
            whereClauses.Add($"MailCategoryAssignment.MailCategoryId IN ({categoryPlaceholders})");
            parameters.AddRange(options.CategoryIds.Select(a => (object)a));
        }

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

        if (pinnedOnly)
        {
            whereClauses.Add("MailCopy.IsPinned = 1");
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
            sql.Append(" ORDER BY IsPinned DESC, CreationDate DESC");
        else if (options.SortingOptionType == SortingOptionType.Sender)
            sql.Append(" ORDER BY IsPinned DESC, FromName ASC, CreationDate DESC");

        // Pagination
        if (!pinnedOnly)
        {
            var limit = options.Take > 0 ? options.Take : ItemLoadCount;
            sql.Append($" LIMIT {limit}");

            if (options.Skip > 0)
            {
                sql.Append($" OFFSET {options.Skip}");
            }
        }

        return (sql.ToString(), parameters.ToArray());
    }

    private static List<MailCopy> ApplyOptionsToPreFetchedMails(MailListInitializationOptions options, bool pinnedOnly = false)
    {
        var allowedFolderIds = options.Folders.Select(f => f.Id).ToHashSet();
        var accountIdsByFolderId = options.Folders
            .Where(folder => folder != null)
            .GroupBy(folder => folder.Id)
            .ToDictionary(group => group.Key, group => group.First().MailAccountId);

        IEnumerable<MailCopy> query = options.PreFetchMailCopies
            .Where(m => m != null && allowedFolderIds.Contains(m.FolderId));

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

        if (pinnedOnly)
        {
            query = query.Where(m => m.IsPinned);
        }

        query = options.DeduplicateByServerId
            ? query
                .GroupBy(m => (ResolveMailAccountId(m, accountIdsByFolderId), ResolveServerMailId(m)))
                .Select(group => group
                    .OrderByDescending(m => allowedFolderIds.Contains(m.FolderId))
                    .ThenByDescending(m => m.CreationDate)
                    .ThenBy(m => m.FolderId)
                    .ThenBy(m => m.UniqueId)
                    .First())
            : query
                .GroupBy(m => m.UniqueId)
                .Select(group => group.First());

        query = options.SortingOptionType switch
        {
            SortingOptionType.Sender => query
                .OrderByDescending(m => m.IsPinned)
                .ThenBy(m => m.FromName)
                .ThenByDescending(m => m.CreationDate),
            _ => query
                .OrderByDescending(m => m.IsPinned)
                .ThenByDescending(m => m.CreationDate)
        };

        if (!pinnedOnly && options.Skip > 0)
        {
            query = query.Skip(options.Skip);
        }

        if (!pinnedOnly && options.Take > 0)
        {
            query = query.Take(options.Take);
        }

        return query.ToList();
    }

    private static Guid ResolveMailAccountId(MailCopy mail, IReadOnlyDictionary<Guid, Guid> accountIdsByFolderId)
    {
        if (mail?.AssignedAccount != null)
            return mail.AssignedAccount.Id;

        if (mail != null && accountIdsByFolderId.TryGetValue(mail.FolderId, out var accountId))
            return accountId;

        return Guid.Empty;
    }

    private static string ResolveServerMailId(MailCopy mail)
        => string.IsNullOrWhiteSpace(mail?.Id) ? mail?.UniqueId.ToString("N") ?? string.Empty : mail.Id;

    public Task<List<MailCopy>> FetchMailsAsync(MailListInitializationOptions options, CancellationToken cancellationToken = default)
        => FetchMailsInternalAsync(options, pinnedOnly: false, cancellationToken);

    public Task<List<MailCopy>> FetchPinnedMailsAsync(MailListInitializationOptions options, CancellationToken cancellationToken = default)
        => FetchMailsInternalAsync(options, pinnedOnly: true, cancellationToken);

    private async Task<List<MailCopy>> FetchMailsInternalAsync(MailListInitializationOptions options, bool pinnedOnly, CancellationToken cancellationToken = default)
    {
        List<MailCopy> mails;

        if (options.PreFetchMailCopies != null && !options.IsCategoryView)
        {
            mails = ApplyOptionsToPreFetchedMails(options, pinnedOnly);
        }
        else
        {
            var (query, parameters) = BuildMailFetchQuery(options, pinnedOnly);
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
        await _sentMailReceiptService.PopulateReceiptStatesAsync(mails).ConfigureAwait(false);

        if (!options.CreateThreads || mails.Count == 0 || options.IsCategoryView)
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

        await _sentMailReceiptService.PopulateReceiptStatesAsync(mails).ConfigureAwait(false);

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
            if (!string.IsNullOrEmpty(mail.FromAddress) &&
                string.Equals(mail.FromAddress, account.Address, StringComparison.OrdinalIgnoreCase))
            {
                if (contactCache.TryGetValue(mail.FromAddress, out var ownContact))
                {
                    mail.SenderContact = ownContact;
                }
                else
                {
                    mail.SenderContact = new AccountContact
                    {
                        Address = account.Address,
                        Name = account.SenderName
                    };
                }
            }
            else
            {
                contactCache.TryGetValue(mail.FromAddress ?? string.Empty, out var contact);
                mail.SenderContact = contact ?? CreateUnknownContact(mail.FromName, mail.FromAddress);
            }
        }
    }

    // AssignedAccount is loaded through AccountService so IMAP server information,
    // preferences, and other account-side ignored properties are populated as well.
    private async Task<List<MailCopy>> HydrateMailCopiesAsync(List<MailCopy> mails)
    {
        if (mails == null || mails.Count == 0)
            return mails ?? [];

        var folderIds = mails
            .Select(m => m.FolderId)
            .Distinct()
            .ToList();

        if (folderIds.Count == 0)
            return mails;

        var folders = await Task.WhenAll(folderIds.Select(id => _folderService.GetFolderAsync(id))).ConfigureAwait(false);
        var folderCache = folders
            .Where(f => f != null)
            .ToDictionary(f => f.Id);

        if (folderCache.Count == 0)
            return mails;

        var accountIds = folderCache.Values
            .Select(f => f.MailAccountId)
            .Distinct()
            .ToHashSet();

        var allAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountCache = allAccounts
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionary(a => a.Id);

        var addresses = mails
            .Where(m => !string.IsNullOrEmpty(m.FromAddress))
            .Select(m => m.FromAddress)
            .Distinct()
            .ToList();

        var contactCache = addresses.Count == 0
            ? new Dictionary<string, AccountContact>()
            : (await _contactService.GetContactsByAddressesAsync(addresses).ConfigureAwait(false))
                .Where(c => c != null)
                .ToDictionary(c => c.Address);

        AssignPropertiesFromCaches(mails, folderCache, accountCache, contactCache);
        await _sentMailReceiptService.PopulateReceiptStatesAsync(mails).ConfigureAwait(false);

        return mails;
    }

    private async Task<MailCopy> HydrateMailCopyAsync(MailCopy mailCopy)
    {
        if (mailCopy == null)
            return null;

        var hydratedMails = await HydrateMailCopiesAsync([mailCopy]).ConfigureAwait(false);
        return hydratedMails.FirstOrDefault();
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

    private async Task<List<MailCopy>> GetMailCopiesByIdAsync(IEnumerable<string> mailCopyIds)
    {
        var distinctMailCopyIds = mailCopyIds?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinctMailCopyIds == null || distinctMailCopyIds.Count == 0)
            return [];

        var mailCopies = new List<MailCopy>();

        const int batchSize = 200;

        for (int i = 0; i < distinctMailCopyIds.Count; i += batchSize)
        {
            var batchIds = distinctMailCopyIds.Skip(i).Take(batchSize).ToList();
            var placeholders = string.Join(",", batchIds.Select(_ => "?"));
            var sql = $"SELECT * FROM MailCopy WHERE Id IN ({placeholders})";

            var batch = await Connection.QueryAsync<MailCopy>(sql, batchIds.Cast<object>().ToArray()).ConfigureAwait(false);
            mailCopies.AddRange(batch);
        }

        return await HydrateMailCopiesAsync(mailCopies).ConfigureAwait(false);
    }

    private Task<AccountContact> GetSenderContactForAccountAsync(MailAccount account, string fromAddress)
    {
        // Make sure to return the latest up to date contact information for the original account.
        if (string.Equals(fromAddress, account.Address, StringComparison.OrdinalIgnoreCase))
        {
            return GetOwnSenderContactAsync(account);
        }
        else
        {
            return _contactService.GetAddressInformationByAddressAsync(fromAddress);
        }
    }

    private async Task<AccountContact> GetOwnSenderContactAsync(MailAccount account)
    {
        var contact = await _contactService.GetAddressInformationByAddressAsync(account.Address).ConfigureAwait(false);

        return contact ?? new AccountContact
        {
            Address = account.Address,
            Name = account.SenderName
        };
    }

    public async Task<MailCopy> GetSingleMailItemWithoutFolderAssignmentAsync(string mailCopyId)
    {
        var mailCopy = await Connection.Table<MailCopy>().FirstOrDefaultAsync(a => a.Id == mailCopyId).ConfigureAwait(false);

        return await HydrateMailCopyAsync(mailCopy).ConfigureAwait(false);
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
            mailCopyId).ConfigureAwait(false);

        return await HydrateMailCopyAsync(mailCopy).ConfigureAwait(false);
    }

    public async Task<MailCopy> GetSingleMailItemAsync(string mailCopyId, string remoteFolderId)
    {
        var mailItem = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.Id = ? AND MailItemFolder.RemoteFolderId = ?",
            mailCopyId, remoteFolderId).ConfigureAwait(false);

        return await HydrateMailCopyAsync(mailItem).ConfigureAwait(false);
    }

    public async Task<MailCopy> GetSingleMailItemAsync(Guid uniqueMailId)
    {
        var mailItem = await Connection.FindAsync<MailCopy>(uniqueMailId).ConfigureAwait(false);

        return await HydrateMailCopyAsync(mailItem).ConfigureAwait(false);
    }

    // v2

    public async Task DeleteMailAsync(Guid accountId, string mailCopyId)
    {
        var allMails = await GetMailCopiesByIdAsync([mailCopyId]).ConfigureAwait(false);

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

        var hydratedMailCopy = await HydrateMailCopyAsync(mailCopy).ConfigureAwait(false);
        ReportUIChange(new MailAddedMessage(hydratedMailCopy, EntityUpdateSource.Server));
    }

    public async Task UpdateMailAsync(MailCopy mailCopy)
    {
        if (mailCopy == null)
        {
            _logger.Warning("Null mail passed to UpdateMailAsync call.");

            return;
        }

        _logger.Debug("Updating mail {MailCopyId} with Folder {FolderId}", mailCopy.Id, mailCopy.FolderId);

        var existingMailCopy = mailCopy.UniqueId != Guid.Empty
            ? await Connection.FindAsync<MailCopy>(mailCopy.UniqueId).ConfigureAwait(false)
            : null;

        if (existingMailCopy != null)
        {
            // Pinning is managed locally for now, so server refreshes should not clear it.
            mailCopy.IsPinned = existingMailCopy.IsPinned;
        }

        await Connection.UpdateAsync(mailCopy, typeof(MailCopy)).ConfigureAwait(false);

        var hydratedMailCopy = await HydrateMailCopyAsync(mailCopy).ConfigureAwait(false);
        ReportUIChange(new MailUpdatedMessage(hydratedMailCopy, EntityUpdateSource.Server));
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
        await Connection.ExecuteAsync("DELETE FROM MailCategoryAssignment WHERE MailCopyUniqueId = ?", mailCopy.UniqueId).ConfigureAwait(false);

        // If there are no more copies exists of the same mail, delete the MIME file as well.
        var isMailExists = await IsMailExistsAsync(mailCopy.Id).ConfigureAwait(false);

        if (!isMailExists && !preserveMimeFile)
        {
            await _mimeFileService.DeleteMimeMessageAsync(mailCopy.AssignedAccount.Id, mailCopy.FileId).ConfigureAwait(false);
        }

        ReportUIChange(new MailRemovedMessage(mailCopy, EntityUpdateSource.Server));
    }

    #endregion

    private async Task PersistMailCopyUpdatesAsync(IReadOnlyList<(MailCopy MailCopy, MailCopyChangeFlags ChangedProperties)> pendingUpdates)
    {
        if (pendingUpdates == null || pendingUpdates.Count == 0)
            return;

        await Connection.RunInTransactionAsync(connection =>
        {
            foreach (var (mailCopy, _) in pendingUpdates)
            {
                connection.Update(mailCopy, typeof(MailCopy));
            }
        }).ConfigureAwait(false);

        var readMailUniqueIds = pendingUpdates
            .Where(x => (x.ChangedProperties & MailCopyChangeFlags.IsRead) != 0 &&
                        x.MailCopy?.IsRead == true &&
                        x.MailCopy.UniqueId != Guid.Empty)
            .Select(x => x.MailCopy.UniqueId)
            .Distinct()
            .ToList();

        if (readMailUniqueIds.Count > 0)
        {
            WeakReferenceMessenger.Default.Send(new BulkMailReadStatusChanged(readMailUniqueIds));
        }

        var hydratedUpdatesByUniqueId = (await HydrateMailCopiesAsync(
                pendingUpdates
                    .Where(x => x.MailCopy != null)
                    .Select(x => x.MailCopy)
                    .GroupBy(x => x.UniqueId)
                    .Select(group => group.First())
                    .ToList())
            .ConfigureAwait(false))
            .Where(x => x != null)
            .ToDictionary(x => x.UniqueId);

        foreach (var updateGroup in pendingUpdates
                     .Where(x => x.MailCopy != null)
                     .GroupBy(x => x.ChangedProperties))
        {
            var updatedMails = updateGroup
                .Select(x => hydratedUpdatesByUniqueId.GetValueOrDefault(x.MailCopy.UniqueId, x.MailCopy))
                .Where(x => x != null)
                .ToList();

            if (updatedMails.Count == 0)
                continue;

            ReportUIChange(new BulkMailUpdatedMessage(updatedMails, EntityUpdateSource.Server, updateGroup.Key));
        }
    }

    private async Task UpdateAllMailCopiesAsync(string mailCopyId, Func<MailCopy, MailCopyChangeFlags> action)
    {
        var mailCopies = await GetMailCopiesByIdAsync([mailCopyId]).ConfigureAwait(false);

        if (mailCopies == null || !mailCopies.Any())
        {
            _logger.Warning("Updating mail copies failed because there are no copies available with Id {MailCopyId}", mailCopyId);

            return;
        }

        _logger.Debug("Updating {MailCopyCount} mail copies with Id {MailCopyId}", mailCopies.Count, mailCopyId);

        var pendingUpdates = new List<(MailCopy MailCopy, MailCopyChangeFlags ChangedProperties)>();

        foreach (var mailCopy in mailCopies)
        {
            var changedProperties = action(mailCopy);

            if (changedProperties != MailCopyChangeFlags.None)
            {
                pendingUpdates.Add((mailCopy, changedProperties));
            }
            else
            {
                _logger.Debug("Skipped updating mail because it is already in the desired state.");
            }
        }

        await PersistMailCopyUpdatesAsync(pendingUpdates).ConfigureAwait(false);
    }

    public Task ChangeReadStatusAsync(string mailCopyId, bool isRead)
        => UpdateAllMailCopiesAsync(mailCopyId, (item) =>
        {
            if (item.IsRead == isRead) return MailCopyChangeFlags.None;

            item.IsRead = isRead;

            return MailCopyChangeFlags.IsRead;
        });

    public Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged)
        => UpdateAllMailCopiesAsync(mailCopyId, (item) =>
        {
            if (item.IsFlagged == isFlagged) return MailCopyChangeFlags.None;

            item.IsFlagged = isFlagged;

            return MailCopyChangeFlags.IsFlagged;
        });

    public async Task ChangePinnedStatusAsync(IEnumerable<Guid> uniqueMailIds, bool isPinned)
    {
        var distinctUniqueIds = uniqueMailIds?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? [];

        if (distinctUniqueIds.Count == 0)
            return;

        var placeholders = string.Join(",", distinctUniqueIds.Select(_ => "?"));
        var mailCopies = await Connection
            .QueryAsync<MailCopy>($"SELECT * FROM MailCopy WHERE UniqueId IN ({placeholders})", distinctUniqueIds.Cast<object>().ToArray())
            .ConfigureAwait(false);

        if (mailCopies.Count == 0)
        {
            _logger.Warning("Changing pin status failed because there are no matching copies for {MailCopyCount} unique ids.", distinctUniqueIds.Count);
            return;
        }

        var pendingUpdates = new List<(MailCopy MailCopy, MailCopyChangeFlags ChangedProperties)>();

        foreach (var mailCopy in mailCopies)
        {
            if (mailCopy.IsPinned == isPinned)
                continue;

            mailCopy.IsPinned = isPinned;
            pendingUpdates.Add((mailCopy, MailCopyChangeFlags.IsPinned));
        }

        await PersistMailCopyUpdatesAsync(pendingUpdates).ConfigureAwait(false);
    }

    public async Task ApplyMailStateUpdatesAsync(IEnumerable<MailCopyStateUpdate> updates)
    {
        var updateLookup = new Dictionary<string, MailCopyStateUpdate>(StringComparer.Ordinal);

        foreach (var update in updates ?? [])
        {
            if (update == null || string.IsNullOrWhiteSpace(update.MailCopyId))
                continue;

            if (updateLookup.TryGetValue(update.MailCopyId, out var existingUpdate))
            {
                updateLookup[update.MailCopyId] = new MailCopyStateUpdate(
                    update.MailCopyId,
                    update.IsRead ?? existingUpdate.IsRead,
                    update.IsFlagged ?? existingUpdate.IsFlagged);
            }
            else
            {
                updateLookup[update.MailCopyId] = update;
            }
        }

        if (updateLookup.Count == 0)
            return;

        var mailCopies = await GetMailCopiesByIdAsync(updateLookup.Keys).ConfigureAwait(false);

        if (mailCopies.Count == 0)
        {
            _logger.Warning("Applying mail state updates failed because there are no matching copies for {MailCopyCount} ids.", updateLookup.Count);
            return;
        }

        var pendingUpdates = new List<(MailCopy MailCopy, MailCopyChangeFlags ChangedProperties)>();

        foreach (var mailCopy in mailCopies)
        {
            if (!updateLookup.TryGetValue(mailCopy.Id, out var update))
                continue;

            var changedProperties = MailCopyChangeFlags.None;

            if (update.IsRead.HasValue && mailCopy.IsRead != update.IsRead.Value)
            {
                mailCopy.IsRead = update.IsRead.Value;
                changedProperties |= MailCopyChangeFlags.IsRead;
            }

            if (update.IsFlagged.HasValue && mailCopy.IsFlagged != update.IsFlagged.Value)
            {
                mailCopy.IsFlagged = update.IsFlagged.Value;
                changedProperties |= MailCopyChangeFlags.IsFlagged;
            }

            if (changedProperties != MailCopyChangeFlags.None)
            {
                pendingUpdates.Add((mailCopy, changedProperties));
            }
        }

        await PersistMailCopyUpdatesAsync(pendingUpdates).ConfigureAwait(false);
    }

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

        if (await IsMailExistsAsync(mailCopyId, localFolder.Id).ConfigureAwait(false))
        {
            _logger.Debug("Skipping assignment creation for {MailCopyId} because folder {FolderId} already has a local copy.",
                mailCopyId, localFolder.Id);
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
        await _sentMailReceiptService.TrackSentMailAsync(mailCopy, mimeMessage).ConfigureAwait(false);
        await _sentMailReceiptService.ProcessIncomingReceiptAsync(mailCopy, mimeMessage).ConfigureAwait(false);
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
            await ReplaceMailCategoriesForPackageAsync(accountId, mailCopy, package).ConfigureAwait(false);
            await _sentMailReceiptService.TrackSentMailAsync(mailCopy, mimeMessage).ConfigureAwait(false);
            await _sentMailReceiptService.ProcessIncomingReceiptAsync(mailCopy, mimeMessage).ConfigureAwait(false);

            return false;
        }
        else
        {
            if (account.ProviderType != MailProviderType.Gmail)
            {
                // Make sure there is only 1 instance left of this mail copy id.
                var allMails = await GetMailCopiesByIdAsync([mailCopy.Id]).ConfigureAwait(false);

                await DeleteMailAsync(accountId, mailCopy.Id).ConfigureAwait(false);
            }

            await InsertMailAsync(mailCopy).ConfigureAwait(false);
            await ReplaceMailCategoriesForPackageAsync(accountId, mailCopy, package).ConfigureAwait(false);
            await _sentMailReceiptService.TrackSentMailAsync(mailCopy, mimeMessage).ConfigureAwait(false);
            await _sentMailReceiptService.ProcessIncomingReceiptAsync(mailCopy, mimeMessage).ConfigureAwait(false);

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

    private Task ReplaceMailCategoriesForPackageAsync(Guid accountId, MailCopy mailCopy, NewMailItemPackage package)
        => package?.CategoryNames == null
            ? Task.CompletedTask
            : _mailCategoryService.ReplaceMailAssignmentsAsync(accountId, mailCopy.UniqueId, package.CategoryNames);

    private async Task<MimeMessage> CreateDraftMimeAsync(MailAccount account, DraftCreationOptions draftCreationOptions, MailAccountAlias selectedAlias)
    {
        // This unique id is stored in mime headers for Wino to identify remote message with local copy.
        // Same unique id will be used for the local copy as well.
        // Synchronizer will map this unique id to the local draft copy after synchronization.

        var message = new MimeMessage()
        {
            Headers = { { Constants.WinoLocalDraftHeader, Guid.NewGuid().ToString() } },
        };
        EnsureOutgoingMessageId(message);

        selectedAlias ??= await _accountService.GetPrimaryAccountAliasAsync(account.Id) ?? throw new MissingAliasException();

        // Set FromName and FromAddress by alias.
        message.From.Add(new MailboxAddress(selectedAlias.AliasSenderName ?? account.SenderName, selectedAlias.AliasAddress));

        if (!string.IsNullOrWhiteSpace(selectedAlias.ReplyToAddress))
        {
            message.ReplyTo.Add(new MailboxAddress(selectedAlias.ReplyToAddress, selectedAlias.ReplyToAddress));
        }

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

    private async Task<MailAccountAlias> ResolveDraftAliasAsync(MailAccount account, DraftCreationOptions draftCreationOptions)
    {
        var aliases = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
        var primaryAlias = aliases.FirstOrDefault(a => a.IsPrimary) ?? aliases.FirstOrDefault();

        if (draftCreationOptions?.ReferencedMessage?.MimeMessage == null)
            return primaryAlias;

        var referencedMessage = draftCreationOptions.ReferencedMessage.MimeMessage;

        MailAccountAlias FindAlias(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return null;

            return aliases.FirstOrDefault(a => a.AliasAddress.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var deliveredToAlias = FindAlias(ExtractAddressFromHeader(referencedMessage.Headers["Delivered-To"]))
            ?? FindAlias(ExtractAddressFromHeader(referencedMessage.Headers["X-Original-To"]));
        if (deliveredToAlias != null)
            return deliveredToAlias;

        foreach (var mailbox in referencedMessage.To.Mailboxes)
        {
            var matchedAlias = FindAlias(mailbox.Address);
            if (matchedAlias != null)
                return matchedAlias;
        }

        foreach (var mailbox in referencedMessage.Cc.Mailboxes)
        {
            var matchedAlias = FindAlias(mailbox.Address);
            if (matchedAlias != null)
                return matchedAlias;
        }

        return primaryAlias;
    }

    private static string ExtractAddressFromHeader(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return string.Empty;

        var trimmed = headerValue.Trim();
        var leftBracketIndex = trimmed.LastIndexOf('<');
        var rightBracketIndex = trimmed.LastIndexOf('>');

        if (leftBracketIndex >= 0 && rightBracketIndex > leftBracketIndex)
            return trimmed[(leftBracketIndex + 1)..rightBracketIndex].Trim();

        return trimmed.Trim().Trim('<', '>', '"', '\'');
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
        var referenceMailCopy = draftCreationOptions.ReferencedMessage.MailCopy;
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

            var referenceMessageId = MailHeaderExtensions.NormalizeMessageId(referenceMessage.Headers[HeaderId.MessageId]);
            if (string.IsNullOrEmpty(referenceMessageId))
                referenceMessageId = MailHeaderExtensions.NormalizeMessageId(referenceMailCopy?.MessageId);

            if (!string.IsNullOrEmpty(referenceMessageId))
            {
                message.InReplyTo = referenceMessageId;

                var existingReferences = referenceMessage.References?.Select(MailHeaderExtensions.NormalizeMessageId).ToList() ?? [];
                if (existingReferences.Count == 0)
                    existingReferences = MailHeaderExtensions.SplitMessageIds(referenceMailCopy?.References).ToList();

                var refs = MailHeaderExtensions.BuildReferencesChain(existingReferences, referenceMessageId);

                foreach (var referenceId in refs)
                    message.References.Add(referenceId);
            }

            if (!string.IsNullOrEmpty(referenceMessage.Subject))
                message.Headers.Add("Thread-Topic", referenceMessage.Subject);
        }

        // Manage Subject
        var referenceSubject = referenceMessage?.Subject ?? string.Empty;
        if (reason == DraftCreationReason.Forward && !referenceSubject.StartsWith("FW: ", StringComparison.OrdinalIgnoreCase))
            message.Subject = $"FW: {referenceSubject}";
        else if ((reason == DraftCreationReason.Reply || reason == DraftCreationReason.ReplyAll) && !referenceSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
            message.Subject = $"Re: {referenceSubject}";
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

        localDraftCopy = await HydrateMailCopyAsync(localDraftCopy).ConfigureAwait(false);

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
                var changedProperties = MailCopyChangeFlags.None;

                if (shouldUpdateDraftId)
                {
                    item.DraftId = newDraftId;
                    changedProperties |= MailCopyChangeFlags.DraftId;
                }

                if (shouldUpdateThreadId)
                {
                    item.ThreadId = newThreadId;
                    changedProperties |= MailCopyChangeFlags.ThreadId;
                }

                ReportUIChange(new DraftMapped(oldDraftId, item.DraftId));

                return changedProperties;
            }

            return MailCopyChangeFlags.None;
        });
    }

    public async Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
    {
        var placeholders = string.Join(",", downloadedMailCopyIds.Select(_ => "?"));
        var sql = $"SELECT MailCopy.* FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.Id IN ({placeholders}) AND MailCopy.IsRead = ? AND MailItemFolder.MailAccountId = ? AND MailItemFolder.SpecialFolderType = ?";
        var parameters = new List<object>();
        parameters.AddRange(downloadedMailCopyIds.Cast<object>());
        parameters.Add(false);
        parameters.Add(accountId);
        parameters.Add((int)SpecialFolderType.Inbox);

        var mailCopies = await Connection.QueryAsync<MailCopy>(sql, parameters.ToArray()).ConfigureAwait(false);
        return await HydrateMailCopiesAsync(mailCopies).ConfigureAwait(false);
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

        var mailCopies = await Connection.QueryAsync<MailCopy>(sql, localMailIds.Cast<object>().ToArray()).ConfigureAwait(false);
        return await HydrateMailCopiesAsync(mailCopies).ConfigureAwait(false);
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

    private static void EnsureOutgoingMessageId(MimeMessage message)
    {
        if (message == null)
            return;

        var messageId = MailHeaderExtensions.NormalizeMessageId(MessageIdGenerator.Generate());

        if (string.IsNullOrEmpty(messageId))
            return;

        var headerValue = MailHeaderExtensions.ToHeaderMessageId(messageId);

        if (message.Headers.Contains(HeaderId.MessageId))
            message.Headers.Remove(HeaderId.MessageId);

        message.Headers.Add(HeaderId.MessageId, headerValue);
        message.MessageId = messageId;
    }

    private static string GetNormalizedMimeMessageId(MimeMessage message)
        => MailHeaderExtensions.NormalizeMessageId(message?.Headers[HeaderId.MessageId]);

    private static string GetNormalizedMimeInReplyTo(MimeMessage message)
    {
        if (message == null)
            return string.Empty;

        var inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo)
            ? message.Headers[HeaderId.InReplyTo]
            : message.InReplyTo;

        return MailHeaderExtensions.NormalizeMessageId(inReplyTo);
    }

    private static string GetNormalizedMimeReferences(MimeMessage message)
    {
        if (message == null)
            return string.Empty;

        if (message.References?.Count > 0)
            return MailHeaderExtensions.JoinStoredReferences(message.References);

        return MailHeaderExtensions.NormalizeReferences(message.Headers[HeaderId.References]);
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

        return await HydrateMailCopiesAsync(mailCopies).ConfigureAwait(false);
    }

    public async Task<List<string>> AreMailsExistsAsync(IEnumerable<string> mailCopyIds)
    {
        var placeholders = string.Join(",", mailCopyIds.Select(_ => "?"));
        var sql = $"SELECT Id FROM MailCopy WHERE Id IN ({placeholders})";

        return await Connection.QueryScalarsAsync<string>(sql, mailCopyIds.Cast<object>().ToArray());
    }

    public async Task<List<MailCopy>> GetMailCopiesBeforeDateAsync(Guid accountId, DateTime cutoffDateUtc)
    {
        const string query = """
                             SELECT MailCopy.*
                             FROM MailCopy
                             INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id
                             WHERE MailItemFolder.MailAccountId = ?
                               AND MailCopy.CreationDate < ?
                             """;

        var mailCopies = await Connection.QueryAsync<MailCopy>(query, accountId, cutoffDateUtc).ConfigureAwait(false);
        return await HydrateMailCopiesAsync(mailCopies).ConfigureAwait(false);
    }
}
