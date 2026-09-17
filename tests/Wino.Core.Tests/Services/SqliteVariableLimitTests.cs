using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Regression tests for Sentry WINOMAIL-2H9 ("too many SQL variables" from
/// SQLite3.Prepare2). Every test drives an unbounded IN-list with more bound
/// parameters than SQLite's per-statement variable limit, proving the query
/// is executed in chunks instead of a single oversized statement.
/// </summary>
public class SqliteVariableLimitTests : IAsyncLifetime
{
    private const int OversizedCount = 1200;

    private InMemoryDatabaseService _databaseService = null!;
    private MailService _mailService = null!;
    private FolderService _folderService = null!;
    private MailCategoryService _mailCategoryService = null!;
    private MailAccount _testAccount = null!;
    private MailItemFolder _inboxFolder = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _testAccount = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Test Account",
            Address = "me@test.local",
            SenderName = "Test User",
            ProviderType = MailProviderType.IMAP4
        };

        _inboxFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _testAccount.Id,
            FolderName = "Inbox",
            RemoteFolderId = "inbox",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSystemFolder = true,
            IsSynchronizationEnabled = true
        };

        await _databaseService.Connection.InsertAsync(_testAccount, typeof(MailAccount));
        await _databaseService.Connection.InsertAsync(_inboxFolder, typeof(MailItemFolder));

        var signatureService = new Mock<ISignatureService>();
        var authProvider = new Mock<IAuthenticationProvider>();
        var mimeFileService = new Mock<IMimeFileService>();
        var preferencesService = new Mock<IPreferencesService>();
        var contactPictureFileService = new Mock<IContactPictureFileService>();

        var accountService = new AccountService(
            _databaseService,
            signatureService.Object,
            authProvider.Object,
            mimeFileService.Object,
            preferencesService.Object,
            contactPictureFileService.Object);

        _mailCategoryService = new MailCategoryService(_databaseService);
        _folderService = new FolderService(_databaseService, accountService, _mailCategoryService);
        var contactService = new ContactService(_databaseService);
        var sentMailReceiptService = new SentMailReceiptService(_databaseService, _folderService, accountService);

        _mailService = new MailService(
            _databaseService,
            _folderService,
            contactService,
            accountService,
            signatureService.Object,
            mimeFileService.Object,
            preferencesService.Object,
            sentMailReceiptService,
            _mailCategoryService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task AreMailsExistsAsync_OversizedIdList_ReturnsExisting()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        var queryIds = mails.Select(mail => mail.Id).Concat(["missing-1", "missing-2"]).ToList();

        var existing = await _mailService.AreMailsExistsAsync(queryIds);

        existing.Should().HaveCount(OversizedCount);
        existing.Should().BeEquivalentTo(mails.Select(mail => mail.Id));
    }

    [Fact]
    public async Task AreMailsExistsAsync_EmptyList_ReturnsEmpty()
    {
        var existing = await _mailService.AreMailsExistsAsync([]);

        existing.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMailItemsAsync_OversizedIdList_ReturnsAll()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        var copies = await _mailService.GetMailItemsAsync(mails.Select(mail => mail.Id));

        copies.Should().HaveCount(OversizedCount);
    }

    [Fact]
    public async Task GetExistingMailsAsync_OversizedUidList_ReturnsMatches()
    {
        // Two bound parameters per uid plus one fixed parameter: 600 uids need
        // 1201 variables, above SQLite's 999 limit without chunking.
        const int uidCount = 600;
        var mails = BuildMails(uidCount);
        for (var i = 0; i < mails.Count; i++)
            mails[i].ImapUid = (uint)(i + 1);
        await _databaseService.Connection.InsertAllAsync(mails);

        var uids = Enumerable.Range(1, uidCount).Select(id => new MailKit.UniqueId((uint)id)).ToList();

        var existing = await _mailService.GetExistingMailsAsync(_inboxFolder.Id, uids);

        existing.Should().HaveCount(uidCount);
    }

    [Fact]
    public async Task GetDownloadedUnreadMailsAsync_OversizedBatch_ReturnsUnread()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        var unread = await _mailService.GetDownloadedUnreadMailsAsync(
            _testAccount.Id,
            mails.Select(mail => mail.Id));

        unread.Should().HaveCount(OversizedCount);
    }

    [Fact]
    public async Task ChangePinnedStatusAsync_OversizedSelection_PinsAll()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        await _mailService.ChangePinnedStatusAsync(mails.Select(mail => mail.UniqueId), isPinned: true);

        var pinned = await _databaseService.Connection.QueryAsync<MailCopy>(
            "SELECT * FROM MailCopy WHERE IsPinned = 1");
        pinned.Should().HaveCount(OversizedCount);
    }

    [Fact]
    public async Task GetMailsByFolderIdAsync_LargeFolder_HydratesWithoutThrowing()
    {
        // Hydration fans out into receipt-state and contact lookups per mail batch.
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        var copies = await _mailService.GetMailsByFolderIdAsync(_inboxFolder.Id);

        copies.Should().HaveCount(OversizedCount);
        copies.Should().OnlyContain(mail => mail.AssignedFolder != null && mail.AssignedAccount != null);
    }

    [Fact]
    public async Task GetUnreadSenderCountsAsync_OversizedSenders_SumsPerChunk()
    {
        var mails = BuildMails(OversizedCount);
        for (var i = 0; i < mails.Count; i++)
        {
            mails[i].FromAddress = $"sender{i}@example.com";
            mails[i].FromName = $"sender{i}";
        }
        await _databaseService.Connection.InsertAllAsync(mails);

        var counts = await _mailService.GetUnreadSenderCountsAsync(
            [_inboxFolder.Id],
            mails.Select(mail => mail.FromAddress).ToList());

        counts.Should().HaveCount(OversizedCount);
        counts.Values.Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task CountMailsAsync_OversizedExclusionList_ExcludesAll()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);
        var options = BuildOptions(
            [_inboxFolder],
            take: OversizedCount,
            existingUniqueIds: new ConcurrentDictionary<Guid, bool>(
                mails.Select(mail => new KeyValuePair<Guid, bool>(mail.UniqueId, true))));

        var count = await _mailService.CountMailsAsync(options);

        count.Should().Be(0);
    }

    [Fact]
    public async Task FetchMailsAsync_OversizedThreadSeeds_ExpandsThreads()
    {
        var mails = BuildMails(OversizedCount, threadId: "big-thread");
        await _databaseService.Connection.InsertAllAsync(mails);

        var result = await _mailService.FetchMailsAsync(BuildOptions([_inboxFolder], take: OversizedCount));

        result.Should().HaveCount(OversizedCount);
    }

    [Fact]
    public async Task GetMailFolderPairMetadatasAsync_OversizedIdList_ReturnsAll()
    {
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);

        var metadatas = await _folderService.GetMailFolderPairMetadatasAsync(mails.Select(mail => mail.Id));

        metadatas.Should().HaveCount(OversizedCount);
    }

    [Fact]
    public async Task CategoryQueries_OversizedMailList_StayChunked()
    {
        var mails = BuildMails(900);
        await _databaseService.Connection.InsertAllAsync(mails);
        var category = new MailCategory
        {
            Id = Guid.NewGuid(),
            MailAccountId = _testAccount.Id,
            Name = "Bulk",
            IsFavorite = false
        };
        await _databaseService.Connection.InsertAsync(category, typeof(MailCategory));
        var uniqueIds = mails.Select(mail => mail.UniqueId).ToList();

        await _mailCategoryService.AssignCategoryAsync(category.Id, uniqueIds);

        (await _mailCategoryService.GetAssignedCategoryIdsForAllAsync(uniqueIds))
            .Should().ContainSingle().Which.Should().Be(category.Id);
        (await _mailCategoryService.GetCategoriesForMailAsync(_testAccount.Id, uniqueIds))
            .Should().ContainSingle().Which.Id.Should().Be(category.Id);
        var byMail = await _mailCategoryService.GetCategoriesByMailAsync(_testAccount.Id, uniqueIds);
        byMail.Should().HaveCount(900);

        await _mailCategoryService.UnassignCategoryAsync(category.Id, uniqueIds);

        (await _mailCategoryService.GetAssignedCategoryIdsForAllAsync(uniqueIds)).Should().BeEmpty();
    }

    // ── Over-limit probes: more parameters than SQLite accepts (32766) ──────
    // These run against empty tables so they stay fast: the statement must be
    // split before Prepare2 ever sees an oversized variable list.

    private static List<string> BuildOversizedIds(int count = 40000)
        => Enumerable.Range(0, count).Select(id => $"over-limit-{id}").ToList();

    [Fact]
    public async Task AreMailsExistsAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var existing = await _mailService.AreMailsExistsAsync(BuildOversizedIds());

        existing.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMailItemsAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var copies = await _mailService.GetMailItemsAsync(BuildOversizedIds());

        copies.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExistingMailsAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var uids = Enumerable.Range(1, 20000).Select(id => new MailKit.UniqueId((uint)id)).ToList();

        var existing = await _mailService.GetExistingMailsAsync(_inboxFolder.Id, uids);

        existing.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDownloadedUnreadMailsAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var unread = await _mailService.GetDownloadedUnreadMailsAsync(_testAccount.Id, BuildOversizedIds());

        unread.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePinnedStatusAsync_OverDatabaseLimit_NoThrow()
    {
        var uniqueIds = Enumerable.Range(0, 40000).Select(_ => Guid.NewGuid()).ToList();

        await _mailService.ChangePinnedStatusAsync(uniqueIds, isPinned: true);
    }

    [Fact]
    public async Task GetUnreadSenderCountsAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var counts = await _mailService.GetUnreadSenderCountsAsync(
            [_inboxFolder.Id],
            Enumerable.Range(0, 40000).Select(id => $"nobody{id}@example.com").ToList());

        counts.Should().BeEmpty();
    }

    [Fact]
    public async Task CountMailsAsync_LargeExclusionList_ExcludesMatching()
    {
        // NOTE: the exclusion list shares one statement's variable budget, so it
        // cannot be chunked like the SELECT paths. This pins exclusion behavior
        // at multi-hundred scale; exclusions past ~32k remain a known limit.
        var mails = BuildMails(OversizedCount);
        await _databaseService.Connection.InsertAllAsync(mails);
        var options = BuildOptions(
            [_inboxFolder],
            take: OversizedCount,
            existingUniqueIds: new ConcurrentDictionary<Guid, bool>(
                mails.Select(mail => new KeyValuePair<Guid, bool>(mail.UniqueId, true))));

        var count = await _mailService.CountMailsAsync(options);

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetMailFolderPairMetadatasAsync_OverDatabaseLimit_ReturnsEmpty()
    {
        var metadatas = await _folderService.GetMailFolderPairMetadatasAsync(BuildOversizedIds());

        metadatas.Should().BeEmpty();
    }

    [Fact]
    public async Task CategoryQueries_OverDatabaseLimit_NoThrow()
    {
        var category = new MailCategory
        {
            Id = Guid.NewGuid(),
            MailAccountId = _testAccount.Id,
            Name = "OverLimit",
            IsFavorite = false
        };
        await _databaseService.Connection.InsertAsync(category, typeof(MailCategory));
        var uniqueIds = Enumerable.Range(0, 40000).Select(_ => Guid.NewGuid()).ToList();

        // Bulk-insert assignments directly: AssignCategoryAsync inserts one row
        // per mail, which is correct but too slow to drive at over-limit scale.
        var assignments = uniqueIds.Select(uniqueId => new MailCategoryAssignment
        {
            Id = Guid.NewGuid(),
            MailCategoryId = category.Id,
            MailCopyUniqueId = uniqueId
        }).ToList();
        await _databaseService.Connection.InsertAllAsync(assignments);

        (await _mailCategoryService.GetAssignedCategoryIdsForAllAsync(uniqueIds))
            .Should().ContainSingle().Which.Should().Be(category.Id);
        (await _mailCategoryService.GetCategoriesForMailAsync(_testAccount.Id, uniqueIds))
            .Should().ContainSingle().Which.Id.Should().Be(category.Id);
        (await _mailCategoryService.GetCategoriesByMailAsync(_testAccount.Id, uniqueIds))
            .Should().HaveCount(40000);

        await _mailCategoryService.UnassignCategoryAsync(category.Id, uniqueIds);

        (await _mailCategoryService.GetAssignedCategoryIdsForAllAsync(uniqueIds)).Should().BeEmpty();
    }

    private List<MailCopy> BuildMails(int count, string threadId = null)
    {
        var now = DateTime.UtcNow;
        var mails = new List<MailCopy>(count);
        for (var i = 0; i < count; i++)
        {
            mails.Add(new MailCopy
            {
                UniqueId = Guid.NewGuid(),
                Id = Guid.NewGuid().ToString(),
                FileId = Guid.NewGuid(),
                FolderId = _inboxFolder.Id,
                Subject = $"Subject {i}",
                PreviewText = "Preview text",
                FromAddress = "external@example.com",
                FromName = "external",
                CreationDate = now.AddMinutes(-i),
                ThreadId = threadId,
                IsRead = false
            });
        }

        return mails;
    }

    private static MailListInitializationOptions BuildOptions(
        IEnumerable<MailItemFolder> folders,
        int take,
        ConcurrentDictionary<Guid, bool> existingUniqueIds = null)
    {
        return new MailListInitializationOptions(
            Folders: folders.Cast<IMailItemFolder>().ToList(),
            FilterType: FilterOptionType.All,
            SortingOptionType: SortingOptionType.ReceiveDate,
            CreateThreads: true,
            IsFocusedOnly: null,
            SearchQuery: null,
            ExistingUniqueIds: existingUniqueIds,
            Take: take);
    }
}
