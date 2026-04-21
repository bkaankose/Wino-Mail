using System.Diagnostics;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;
using Xunit.Abstractions;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Integration tests for MailService.FetchMailsAsync that verify the correctness of
/// thread expansion and contact resolution, and track performance for large inboxes.
///
/// All tests run against a real in-memory SQLite file via the full service stack
/// (MailService → FolderService / AccountService / ContactService) so that the
/// batch-query path introduced in the performance optimisation is exercised end-to-end.
/// </summary>
public class MailFetchingTests : IAsyncLifetime
{
    // ── Infrastructure ─────────────────────────────────────────────────────────

    private readonly ITestOutputHelper _output;
    private InMemoryDatabaseService _databaseService = null!;
    private MailService _mailService = null!;
    private MailAccount _testAccount = null!;
    private MailItemFolder _inboxFolder = null!;

    public MailFetchingTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

        _mailService = BuildMailService(_databaseService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    // ── Correctness: threading ON ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that thread siblings which fall outside the initial SQL page are
    /// fetched by the expansion step, so every thread is always fully represented.
    ///
    /// Setup: 2 threads of 3 mails each (6 mails total), page size = 4.
    /// The main query retrieves Thread A (3 mails, newest) and Thread B mail 1 (position 4).
    /// Thread expansion must then fetch Thread B mails 2-3 that were beyond the page.
    /// </summary>
    [Fact]
    public async Task FetchMailsAsync_WithThreadingEnabled_ExpandsSiblingsOutsidePage()
    {
        const int PageSize = 4;
        var threadA = Guid.NewGuid().ToString();
        var threadB = Guid.NewGuid().ToString();
        var baseDate = DateTime.UtcNow;

        var mails = new List<MailCopy>
        {
            // Thread A – all 3 land within the first page (positions 1–3)
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-1), threadId: threadA),
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-2), threadId: threadA),
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-3), threadId: threadA),
            // Thread B – only position 4 lands in the page; 5 and 6 must be expanded
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-4), threadId: threadB),
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-5), threadId: threadB),
            BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-6), threadId: threadB)
        };
        await _databaseService.Connection.InsertAllAsync(mails, typeof(MailCopy));

        var options = BuildOptions([_inboxFolder], createThreads: true, take: PageSize);

        // Act
        var result = await _mailService.FetchMailsAsync(options);

        // Assert – all 6 mails returned even though the page only held 4
        result.Should().HaveCount(6,
            "the 2 Thread B siblings outside the initial page must be fetched by expansion");
        result.Should().OnlyContain(m => m.AssignedAccount != null && m.AssignedFolder != null,
            "every returned mail must have its account and folder resolved");
        result.Count(m => m.ThreadId == threadA).Should().Be(3, "Thread A must be complete");
        result.Count(m => m.ThreadId == threadB).Should().Be(3, "Thread B must be complete");
    }

    // ── Correctness: threading OFF ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that when threading is disabled the result exactly matches the raw
    /// SQL page — no sibling expansion occurs.
    /// </summary>
    [Fact]
    public async Task FetchMailsAsync_WithThreadingDisabled_NeverExpandsSiblings()
    {
        const int PageSize = 4;
        var threadId = Guid.NewGuid().ToString();
        var baseDate = DateTime.UtcNow;

        // 6 mails all sharing a ThreadId; with threading OFF only the first 4 come back
        var mails = Enumerable.Range(0, 6)
            .Select(i => BuildMail(_inboxFolder.Id, baseDate.AddSeconds(-i), threadId: threadId))
            .ToList();

        await _databaseService.Connection.InsertAllAsync(mails, typeof(MailCopy));

        var options = BuildOptions([_inboxFolder], createThreads: false, take: PageSize);

        // Act
        var result = await _mailService.FetchMailsAsync(options);

        // Assert – exactly the page size; no expansion happened
        result.Should().HaveCount(PageSize,
            "with threading disabled the result must match the raw page size");
        result.Should().OnlyContain(m => m.AssignedAccount != null && m.AssignedFolder != null);
    }

    // ── Correctness: contact resolution ───────────────────────────────────────

    /// <summary>
    /// Verifies that sender contacts are resolved from three distinct paths:
    /// the contact store (known sender), the unknown-sender fallback, and the
    /// account-metadata shortcut used for self-sent mails.
    /// </summary>
    [Fact]
    public async Task FetchMailsAsync_SenderContact_ResolvesFromAllThreeSources()
    {
        const string KnownAddress = "known@example.com";
        const string UnknownAddress = "unknown@example.com";

        await _databaseService.Connection.InsertAsync(
            new AccountContact { Address = KnownAddress, Name = "Known Sender" },
            typeof(AccountContact));

        var mails = new List<MailCopy>
        {
            BuildMail(_inboxFolder.Id, DateTime.UtcNow,               fromAddress: KnownAddress),
            BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddSeconds(-1), fromAddress: UnknownAddress),
            BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddSeconds(-2), fromAddress: _testAccount.Address)
        };
        await _databaseService.Connection.InsertAllAsync(mails, typeof(MailCopy));

        var options = BuildOptions([_inboxFolder], createThreads: false, take: 10);

        // Act
        var result = await _mailService.FetchMailsAsync(options);

        result.Should().HaveCount(3);

        // Known contact – resolved from AccountContact table
        var knownResult = result.Single(m => m.FromAddress == KnownAddress);
        knownResult.SenderContact!.Name.Should().Be("Known Sender");

        // Unknown address – falls back to an ad-hoc contact built from From headers
        var unknownResult = result.Single(m => m.FromAddress == UnknownAddress);
        unknownResult.SenderContact!.Address.Should().Be(UnknownAddress);

        // Self-sent mail – contact built from account metadata, not the contact store
        var selfResult = result.Single(m => m.FromAddress == _testAccount.Address);
        selfResult.SenderContact!.Name.Should().Be(_testAccount.SenderName,
            "self-sent mail must use account metadata for the sender contact");
    }

    [Fact]
    public async Task FetchMailsAsync_PreFetchedOnlineSearch_DeduplicatesByServerIdWithinAccount()
    {
        var archiveFolder = await CreateFolderAsync(_testAccount, "Archive", "archive", SpecialFolderType.Archive);
        var sharedId = "server-mail-1";
        var olderCopy = BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddMinutes(-5));
        olderCopy.Id = sharedId;
        var newerCopy = BuildMail(archiveFolder.Id, DateTime.UtcNow);
        newerCopy.Id = sharedId;

        var options = BuildOptions([_inboxFolder, archiveFolder], createThreads: false, deduplicateByServerId: true) with
        {
            PreFetchMailCopies = [olderCopy, newerCopy]
        };

        var result = await _mailService.FetchMailsAsync(options);

        result.Should().HaveCount(1, "online search should show one visible result per server message within an account");
        result.Single().UniqueId.Should().Be(newerCopy.UniqueId, "the newest copy should win when the searched folders tie");
    }

    [Fact]
    public async Task FetchMailsAsync_PreFetchedOnlineSearch_KeepsSameServerIdAcrossAccountsSeparate()
    {
        var secondAccount = await CreateAccountAsync("Second Account", "second@test.local");
        var secondInbox = await CreateFolderAsync(secondAccount, "Inbox", "inbox-2", SpecialFolderType.Inbox);
        const string sharedId = "server-mail-2";

        var firstAccountCopy = BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddMinutes(-1));
        firstAccountCopy.Id = sharedId;

        var secondAccountCopy = BuildMail(secondInbox.Id, DateTime.UtcNow);
        secondAccountCopy.Id = sharedId;

        var options = BuildOptions([_inboxFolder, secondInbox], createThreads: false, deduplicateByServerId: true) with
        {
            PreFetchMailCopies = [firstAccountCopy, secondAccountCopy]
        };

        var result = await _mailService.FetchMailsAsync(options);

        result.Should().HaveCount(2, "dedupe should be scoped per account, not just per server id string");
        result.Select(m => m.AssignedAccount!.Id).Should().BeEquivalentTo([_testAccount.Id, secondAccount.Id]);
    }

    [Fact]
    public async Task FetchMailsAsync_PreFetchedOnlineSearch_PrefersActiveFolderCopy()
    {
        var archiveFolder = await CreateFolderAsync(_testAccount, "Archive", "archive-active", SpecialFolderType.Archive);
        const string sharedId = "server-mail-3";

        var activeFolderCopy = BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddMinutes(-5));
        activeFolderCopy.Id = sharedId;

        var newerNonActiveCopy = BuildMail(archiveFolder.Id, DateTime.UtcNow);
        newerNonActiveCopy.Id = sharedId;

        var options = BuildOptions([_inboxFolder], createThreads: false, deduplicateByServerId: true) with
        {
            PreFetchMailCopies = [activeFolderCopy, newerNonActiveCopy]
        };

        var result = await _mailService.FetchMailsAsync(options);

        result.Should().HaveCount(1);
        result.Single().FolderId.Should().Be(_inboxFolder.Id, "a copy from the actively searched folder should win over newer non-searched copies");
    }

    [Fact]
    public async Task FetchPinnedMailsAsync_ReturnsPinnedMailsOutsideRegularPage()
    {
        var oldPinned = BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddDays(-5));
        oldPinned.IsPinned = true;

        var recentMails = Enumerable.Range(0, 120)
            .Select(i => BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddMinutes(-i)))
            .ToList();

        await _databaseService.Connection.InsertAsync(oldPinned, typeof(MailCopy));
        await _databaseService.Connection.InsertAllAsync(recentMails, typeof(MailCopy));

        var options = BuildOptions([_inboxFolder], createThreads: false, take: 20);

        var result = await _mailService.FetchPinnedMailsAsync(options);

        result.Should().ContainSingle(mail => mail.UniqueId == oldPinned.UniqueId);
    }

    [Fact]
    public async Task CreateAssignmentAsync_ExistingAssignment_IsIgnored()
    {
        var archiveFolder = await CreateFolderAsync(_testAccount, "Archive", "archive-existing", SpecialFolderType.Archive);
        const string sharedId = "server-mail-4";

        await _databaseService.Connection.InsertAllAsync(new[]
        {
            BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddMinutes(-1), id: sharedId),
            BuildMail(archiveFolder.Id, DateTime.UtcNow, id: sharedId)
        });

        await _mailService.CreateAssignmentAsync(_testAccount.Id, sharedId, archiveFolder.RemoteFolderId);

        var count = await _databaseService.Connection.Table<MailCopy>().Where(mail => mail.Id == sharedId).CountAsync();
        count.Should().Be(2, "re-creating an existing folder assignment must not insert another row");
    }

    [Fact]
    public async Task CreateAssignmentAsync_NewAssignment_CreatesAdditionalRow()
    {
        var archiveFolder = await CreateFolderAsync(_testAccount, "Archive", "archive-new", SpecialFolderType.Archive);
        const string sharedId = "server-mail-5";

        await _databaseService.Connection.InsertAsync(
            BuildMail(_inboxFolder.Id, DateTime.UtcNow, id: sharedId),
            typeof(MailCopy));

        await _mailService.CreateAssignmentAsync(_testAccount.Id, sharedId, archiveFolder.RemoteFolderId);

        var insertedCopies = await _databaseService.Connection.Table<MailCopy>()
            .Where(mail => mail.Id == sharedId)
            .ToListAsync();

        insertedCopies.Should().HaveCount(2, "adding a new folder assignment should still clone one additional local row");
        insertedCopies.Select(mail => mail.FolderId).Should().BeEquivalentTo([_inboxFolder.Id, archiveFolder.Id]);
    }

    [Fact]
    public async Task UpdateMailAsync_PreservesLocalPinnedState()
    {
        var existingMail = BuildMail(_inboxFolder.Id, DateTime.UtcNow.AddHours(-1));
        existingMail.IsPinned = true;

        await _databaseService.Connection.InsertAsync(existingMail, typeof(MailCopy));

        var refreshedMail = BuildMail(_inboxFolder.Id, DateTime.UtcNow, id: existingMail.Id);
        refreshedMail.UniqueId = existingMail.UniqueId;
        refreshedMail.FileId = existingMail.FileId;
        refreshedMail.Subject = "Updated subject";

        await _mailService.UpdateMailAsync(refreshedMail);

        var storedMail = await _databaseService.Connection.FindAsync<MailCopy>(existingMail.UniqueId);
        storedMail.Should().NotBeNull();
        storedMail!.IsPinned.Should().BeTrue();
        storedMail.Subject.Should().Be("Updated subject");
    }

    // ── Performance: 1 000 mails / ~70 threads ─────────────────────────────────

    /// <summary>
    /// Creates 1 000 mails: 70 threads of 7 mails each (490 mails) plus 510 standalone.
    /// The mails are ordered newest-first in thread blocks so the default first-page
    /// fetch (100 mails) naturally spans several complete threads and the tail of one
    /// partial thread, letting us observe thread expansion.
    ///
    /// Two scenarios are measured and written to test output:
    ///   1. First-page fetch (100 mails) plus automatic thread expansion.
    ///   2. Full load of all 1 000 mails with threading enabled.
    ///
    /// A generous 5-second budget is asserted to catch catastrophic regressions
    /// without being brittle on slow CI hardware.
    /// </summary>
    [Fact]
    public async Task FetchMailsAsync_1000Mails_70Threads_CompletesWithinBudget()
    {
        // ── Arrange ────────────────────────────────────────────────────────────
        const int ThreadCount = 70;
        const int MailsPerThread = 7;
        const int TotalMails = 1_000;
        const int StandaloneMails = TotalMails - (ThreadCount * MailsPerThread); // 510

        // 40 rotating sender addresses; the first 20 have entries in the contact store.
        var senders = Enumerable.Range(0, 40)
            .Select(i => $"sender{i:D2}@example.com")
            .ToList();

        var knownContacts = senders.Take(20)
            .Select((addr, i) => new AccountContact { Address = addr, Name = $"Sender {i}" })
            .ToList();
        await _databaseService.Connection.InsertAllAsync(knownContacts, typeof(AccountContact));

        // Threads occupy the newest date slots (positions 0–489) so the default 100-mail
        // page always intersects several threads, triggering sibling expansion.
        var mails = new List<MailCopy>(TotalMails);
        var baseDate = DateTime.UtcNow;
        int slot = 0;

        for (int t = 0; t < ThreadCount; t++)
        {
            var threadId = Guid.NewGuid().ToString();
            for (int m = 0; m < MailsPerThread; m++)
            {
                mails.Add(BuildMail(
                    _inboxFolder.Id,
                    baseDate.AddSeconds(-slot),
                    threadId: threadId,
                    fromAddress: senders[slot % senders.Count]));
                slot++;
            }
        }

        for (int i = 0; i < StandaloneMails; i++)
        {
            mails.Add(BuildMail(
                _inboxFolder.Id,
                baseDate.AddSeconds(-slot),
                fromAddress: senders[slot % senders.Count]));
            slot++;
        }

        await _databaseService.Connection.InsertAllAsync(mails, typeof(MailCopy));

        _output.WriteLine($"Inserted {TotalMails} mails — " +
                          $"{ThreadCount} threads × {MailsPerThread} mails + {StandaloneMails} standalone");
        _output.WriteLine(string.Empty);

        // ── Scenario 1: first page (default 100) + thread expansion ───────────
        // The 100 newest mails span threads 0–13 completely (14 × 7 = 98 mails) plus
        // the first 2 mails of thread 14.  Expansion must fetch thread 14's 5 siblings.
        var optionsPage = BuildOptions([_inboxFolder], createThreads: true);
        var sw = Stopwatch.StartNew();
        var pageResult = await _mailService.FetchMailsAsync(optionsPage);
        sw.Stop();
        long pageMs = sw.ElapsedMilliseconds;

        _output.WriteLine("[Scenario 1 – first page + thread expansion]");
        _output.WriteLine($"  Mails returned : {pageResult.Count}  (expected > 100)");
        _output.WriteLine($"  Elapsed        : {pageMs} ms");
        _output.WriteLine(string.Empty);

        pageResult.Should().OnlyContain(m => m.AssignedAccount != null && m.AssignedFolder != null);

        // Thread expansion must have added thread 14's 5 siblings beyond the 100-mail page.
        pageResult.Count.Should().BeGreaterThan(100,
            "thread expansion must pull in siblings that were beyond the initial 100-mail page");

        // ── Scenario 2: full load of all 1 000 mails with threading ───────────
        var optionsAll = BuildOptions([_inboxFolder], createThreads: true, take: TotalMails);
        sw.Restart();
        var allResult = await _mailService.FetchMailsAsync(optionsAll);
        sw.Stop();
        long allMs = sw.ElapsedMilliseconds;

        _output.WriteLine($"[Scenario 2 – full load ({TotalMails} mails, threading enabled)]");
        _output.WriteLine($"  Mails returned : {allResult.Count}  (expected {TotalMails})");
        _output.WriteLine($"  Elapsed        : {allMs} ms");

        allResult.Should().HaveCount(TotalMails,
            "every mail must be returned when Take equals the total count");
        allResult.Should().OnlyContain(m => m.AssignedAccount != null && m.AssignedFolder != null);

        // All 70 threads must be intact in the full result.
        var threadGroups = allResult
            .Where(m => !string.IsNullOrEmpty(m.ThreadId))
            .GroupBy(m => m.ThreadId!)
            .ToList();

        threadGroups.Should().HaveCount(ThreadCount,
            "all 70 threads must be represented in the full load");
        threadGroups.Should().OnlyContain(g => g.Count() == MailsPerThread,
            "every thread must contain exactly the expected number of mails");

        allMs.Should().BeLessThan(5_000,
            $"fetching {TotalMails} threaded mails via batched SQLite queries should complete well under 5 s");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static MailCopy BuildMail(
        Guid folderId,
        DateTime creationDate,
        string? threadId = null,
        string fromAddress = "external@example.com",
        string? id = null)
    {
        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = id ?? Guid.NewGuid().ToString(),
            FileId = Guid.NewGuid(),
            FolderId = folderId,
            Subject = $"Subject {Guid.NewGuid():N}",
            PreviewText = "Preview text",
            FromAddress = fromAddress,
            FromName = fromAddress.Split('@')[0],
            CreationDate = creationDate,
            ThreadId = threadId,
            IsRead = false
        };
    }

    private static MailListInitializationOptions BuildOptions(
        IEnumerable<MailItemFolder> folders,
        bool createThreads = true,
        int take = 0,
        bool deduplicateByServerId = false)
    {
        return new MailListInitializationOptions(
            Folders: folders,
            FilterType: FilterOptionType.All,
            SortingOptionType: SortingOptionType.ReceiveDate,
            CreateThreads: createThreads,
            IsFocusedOnly: null,
            SearchQuery: null,
            DeduplicateByServerId: deduplicateByServerId,
            Take: take);
    }

    private async Task<MailAccount> CreateAccountAsync(string name, string address)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = name,
            Address = address,
            SenderName = name,
            ProviderType = MailProviderType.IMAP4
        };

        await _databaseService.Connection.InsertAsync(account, typeof(MailAccount));
        return account;
    }

    private async Task<MailItemFolder> CreateFolderAsync(MailAccount account, string name, string remoteFolderId, SpecialFolderType specialFolderType)
    {
        var folder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            FolderName = name,
            RemoteFolderId = remoteFolderId,
            SpecialFolderType = specialFolderType,
            IsSystemFolder = true,
            IsSynchronizationEnabled = true
        };

        await _databaseService.Connection.InsertAsync(folder, typeof(MailItemFolder));
        return folder;
    }

    /// <summary>
    /// Builds a MailService wired to real FolderService, AccountService, and ContactService
    /// all backed by the shared in-memory database, so the full SQL batch path is exercised.
    /// </summary>
    private static MailService BuildMailService(InMemoryDatabaseService db)
    {
        var signatureService = new Mock<ISignatureService>();
        var authProvider = new Mock<IAuthenticationProvider>();
        var mimeFileService = new Mock<IMimeFileService>();
        var preferencesService = new Mock<IPreferencesService>();
        var contactPictureFileService = new Mock<IContactPictureFileService>();

        var accountService = new AccountService(
            db,
            signatureService.Object,
            authProvider.Object,
            mimeFileService.Object,
            preferencesService.Object,
            contactPictureFileService.Object);

        var mailCategoryService = new MailCategoryService(db);
        var folderService = new FolderService(db, accountService, mailCategoryService);
        var contactService = new ContactService(db);
        var sentMailReceiptService = new SentMailReceiptService(db, folderService, accountService);

        return new MailService(
            db,
            folderService,
            contactService,
            accountService,
            signatureService.Object,
            mimeFileService.Object,
            preferencesService.Object,
            sentMailReceiptService,
            mailCategoryService);
    }
}
