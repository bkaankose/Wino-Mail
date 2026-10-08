using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Mail ingestion feeds recipient history: senders of received mail, recipients of sent mail,
/// each message counted once however many folder or label copies arrive.
/// </summary>
public class MailRecipientHistoryTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private MailService _mailService = null!;
    private MailAccount _account = null!;
    private MailItemFolder _inbox = null!;
    private MailItemFolder _sent = null!;
    private MailItemFolder _junk = null!;
    private MailItemFolder _archive = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Address = "me@test.local",
            SenderName = "Me",
            ProviderType = MailProviderType.IMAP4
        };
        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));
        await _databaseService.Connection.InsertAsync(
            new MailAccountAlias { Id = Guid.NewGuid(), AccountId = _account.Id, AliasAddress = "alias@test.local" },
            typeof(MailAccountAlias));

        _inbox = await AddFolderAsync("INBOX", SpecialFolderType.Inbox);
        _sent = await AddFolderAsync("Sent", SpecialFolderType.Sent);
        _junk = await AddFolderAsync("Junk", SpecialFolderType.Junk);
        _archive = await AddFolderAsync("Archive", SpecialFolderType.Archive);

        _mailService = MailCopyPersistenceTests.BuildMailService(
            _databaseService,
            recipientHistoryService: new RecipientHistoryService(_databaseService));
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task ReceivedMail_RecordsTheSender()
    {
        await _mailService.CreateMailAsync(_account.Id, Package("m1", _inbox, "alice@example.com", "Alice Example"));

        var row = (await GetHistoryAsync()).Should().ContainSingle().Subject;
        row.Address.Should().Be("alice@example.com");
        row.DisplayName.Should().Be("Alice Example");
        row.ReceivedCount.Should().Be(1);
        row.SentCount.Should().Be(0);
    }

    [Fact]
    public async Task SentMail_RecordsRecipients_WithoutTheAccountsOwnAddresses()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Me", "me@test.local"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Cc.Add(new MailboxAddress("Me again", "alias@test.local"));
        mime.Bcc.Add(new MailboxAddress("Carol", "carol@example.com"));

        await _mailService.CreateMailAsync(_account.Id, Package("m2", _sent, "me@test.local", "Me", mime));

        var rows = await GetHistoryAsync();
        rows.Select(row => row.Address).Should().BeEquivalentTo("bob@example.com", "carol@example.com");
        rows.Should().OnlyContain(row => row.SentCount == 1 && row.ReceivedCount == 0);
    }

    [Fact]
    public async Task SentMailWithoutMime_UsesTheExtractedAddresses()
    {
        var contacts = new List<AccountContact>
        {
            new() { Address = "me@test.local", Name = "Me" },
            new() { Address = "dave@example.com", Name = "Dave" }
        };

        // From one of the account's addresses, even outside the Sent folder: a reply filed in the inbox thread.
        await _mailService.CreateMailAsync(_account.Id, Package("m3", _inbox, "alias@test.local", "Me", extractedContacts: contacts));

        (await GetHistoryAsync()).Should().ContainSingle()
            .Which.Should().Match<RecipientHistory>(row => row.Address == "dave@example.com" && row.SentCount == 1);
    }

    [Fact]
    public async Task TheSameMessageInASecondFolder_IsCountedOnce()
    {
        await _mailService.CreateMailAsync(_account.Id, Package("m4", _inbox, "erin@example.com", "Erin", messageId: "<shared@example.com>"));
        await _mailService.CreateMailAsync(_account.Id, Package("m4-archive", _archive, "erin@example.com", "Erin", messageId: "<shared@example.com>"));

        (await GetHistoryAsync()).Single().ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task Resynchronizing_AnExistingMail_DoesNotCountAgain()
    {
        await _mailService.CreateMailAsync(_account.Id, Package("m5", _inbox, "frank@example.com", "Frank"));
        await _mailService.CreateMailAsync(_account.Id, Package("m5", _inbox, "frank@example.com", "Frank"));

        (await GetHistoryAsync()).Single().ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task GmailLabelCopiesInOneBatch_AreCountedOnce()
    {
        _account.ProviderType = MailProviderType.Gmail;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        await _mailService.CreateMailsAsync(_account.Id,
        [
            Package("g1", _inbox, "grace@example.com", "Grace Hopper"),
            Package("g1", _archive, "grace@example.com", "Grace Hopper")
        ]);

        (await GetHistoryAsync()).Single().ReceivedCount.Should().Be(1);

        // A later label copy of the same message is not counted either.
        await _mailService.CreateMailsAsync(_account.Id, [Package("g1", _sent, "grace@example.com", "Grace Hopper")]);
        (await GetHistoryAsync()).Single().ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task JunkAndDrafts_AreIgnored()
    {
        await _mailService.CreateMailAsync(_account.Id, Package("j1", _junk, "heidi@example.com", "Heidi"));
        var draft = Package("d1", _inbox, "ivan@example.com", "Ivan");
        draft.Copy.IsDraft = true;
        await _mailService.CreateMailAsync(_account.Id, draft);

        (await GetHistoryAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AutomatedSenders_AreIgnored()
    {
        await _mailService.CreateMailAsync(_account.Id, Package("n1", _inbox, "noreply@example.com", "Example"));
        await _mailService.CreateMailAsync(_account.Id, Package("n2", _inbox, "a23asd21asdju12398asdf9nfg9hwe@google.com", null));

        (await GetHistoryAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Ingestion_NoLongerCreatesContactCards()
    {
        var contacts = new List<AccountContact> { new() { Address = "judy@example.com", Name = "Judy" } };
        await _mailService.CreateMailAsync(_account.Id, Package("c1", _inbox, "judy@example.com", "Judy", extractedContacts: contacts));

        (await _databaseService.Connection.Table<AccountContact>().CountAsync()).Should().Be(0);
    }

    private NewMailItemPackage Package(
        string id,
        MailItemFolder folder,
        string fromAddress,
        string fromName,
        MimeMessage mime = null,
        IReadOnlyList<AccountContact> extractedContacts = null,
        string messageId = null)
        => new(
            new MailCopy
            {
                Id = id,
                FileId = Guid.NewGuid(),
                FromAddress = fromAddress,
                FromName = fromName,
                MessageId = messageId,
                Subject = id,
                CreationDate = DateTime.UtcNow
            },
            mime,
            folder.RemoteFolderId,
            extractedContacts);

    private async Task<MailItemFolder> AddFolderAsync(string remoteId, SpecialFolderType type)
    {
        var folder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = remoteId,
            RemoteFolderId = remoteId,
            SpecialFolderType = type,
            IsSynchronizationEnabled = true
        };
        await _databaseService.Connection.InsertAsync(folder, typeof(MailItemFolder));
        return folder;
    }

    private Task<List<RecipientHistory>> GetHistoryAsync()
        => _databaseService.Connection.Table<RecipientHistory>().Where(row => row.AccountId == _account.Id).ToListAsync();
}
