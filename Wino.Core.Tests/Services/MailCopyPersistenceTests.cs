using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailCopyPersistenceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private MailService _mailService = null!;
    private MailAccount _account = null!;
    private MailItemFolder _inboxFolder = null!;
    private MailItemFolder _deletedFolder = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Test",
            Address = "me@test.local",
            SenderName = "Test User",
            ProviderType = MailProviderType.IMAP4
        };

        _inboxFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Inbox",
            RemoteFolderId = "INBOX",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSynchronizationEnabled = true
        };

        _deletedFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Deleted",
            RemoteFolderId = "Deleted",
            SpecialFolderType = SpecialFolderType.Deleted,
            IsSynchronizationEnabled = true
        };

        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));
        await _databaseService.Connection.InsertAsync(_inboxFolder, typeof(MailItemFolder));
        await _databaseService.Connection.InsertAsync(_deletedFolder, typeof(MailItemFolder));

        _mailService = BuildMailService(_databaseService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task CreateMailAsync_ForImapMessageIdInOtherFolder_RemovesStaleFolderCopy()
    {
        const string messageId = "same-message@test.local";
        var existingFileId = Guid.NewGuid();

        await _databaseService.Connection.InsertAsync(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = MailkitClientExtensions.CreateUid(_inboxFolder.Id, 10),
            ImapUid = 10,
            ImapUidValidity = 123,
            FolderId = _inboxFolder.Id,
            MessageId = messageId,
            FileId = existingFileId,
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Hello",
            CreationDate = DateTime.UtcNow
        }, typeof(MailCopy));

        var deletedCopy = new MailCopy
        {
            Id = MailkitClientExtensions.CreateUid(_deletedFolder.Id, 77),
            ImapUid = 77,
            ImapUidValidity = 456,
            MessageId = messageId,
            FileId = Guid.NewGuid(),
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Hello",
            CreationDate = DateTime.UtcNow
        };

        var inserted = await _mailService.CreateMailAsync(
            _account.Id,
            new NewMailItemPackage(deletedCopy, null, _deletedFolder.RemoteFolderId));

        inserted.Should().BeTrue();

        var allCopies = await _databaseService.Connection.Table<MailCopy>().ToListAsync();
        allCopies.Should().ContainSingle();
        allCopies[0].FolderId.Should().Be(_deletedFolder.Id);
        allCopies[0].MessageId.Should().Be(messageId);
        allCopies[0].FileId.Should().Be(existingFileId);
    }

    [Fact]
    public async Task CreateMailAsync_ForImapMissingMessageId_KeepsOtherFolderCopy()
    {
        await _databaseService.Connection.InsertAsync(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = MailkitClientExtensions.CreateUid(_inboxFolder.Id, 10),
            ImapUid = 10,
            FolderId = _inboxFolder.Id,
            FileId = Guid.NewGuid(),
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Hello",
            CreationDate = DateTime.UtcNow
        }, typeof(MailCopy));

        var deletedCopy = new MailCopy
        {
            Id = MailkitClientExtensions.CreateUid(_deletedFolder.Id, 77),
            ImapUid = 77,
            FileId = Guid.NewGuid(),
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Hello",
            CreationDate = DateTime.UtcNow
        };

        var inserted = await _mailService.CreateMailAsync(
            _account.Id,
            new NewMailItemPackage(deletedCopy, null, _deletedFolder.RemoteFolderId));

        inserted.Should().BeTrue();

        var allCopies = await _databaseService.Connection.Table<MailCopy>().ToListAsync();
        allCopies.Should().HaveCount(2);
    }

    private static MailService BuildMailService(InMemoryDatabaseService db)
    {
        var signatureService = new Mock<ISignatureService>();
        var authProvider = new Mock<IAuthenticationProvider>();
        var mimeFileService = new Mock<IMimeFileService>();
        mimeFileService
            .Setup(x => x.SaveMimeMessageAsync(It.IsAny<Guid>(), It.IsAny<MimeMessage>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);
        mimeFileService
            .Setup(x => x.CreateHTMLPreviewVisitor(It.IsAny<MimeMessage>(), It.IsAny<string>()))
            .Returns<MimeMessage, string>((_, _) => new HtmlPreviewVisitor(string.Empty));

        var preferencesService = new Mock<IPreferencesService>();
        preferencesService.SetupProperty(x => x.ComposerFont, "Calibri");
        preferencesService.SetupProperty(x => x.ComposerFontSize, 12);

        var accountService = new AccountService(
            db,
            signatureService.Object,
            authProvider.Object,
            mimeFileService.Object,
            preferencesService.Object,
            Mock.Of<IContactPictureFileService>());

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
