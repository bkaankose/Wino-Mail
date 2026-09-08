using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Messaging.UI;
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
    public async Task DelayedDraftStateUpdate_PreservesLatestSavedContent()
    {
        var uniqueId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var current = new MailCopy
        {
            UniqueId = uniqueId, Id = "remote-draft", FolderId = _inboxFolder.Id,
            IsDraft = true, Subject = "Newest local subject", FileId = fileId
        };
        await _databaseService.Connection.InsertAsync(current, typeof(MailCopy));

        var stale = new MailCopy
        {
            UniqueId = uniqueId, Id = current.Id, FolderId = _inboxFolder.Id,
            IsDraft = true, Subject = "Old remote subject", FileId = Guid.NewGuid(), IsRead = true
        };
        var method = typeof(MailService).GetMethod("PersistMailCopyUpdatesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = new List<(MailCopy, MailCopyChangeFlags)> { (stale, MailCopyChangeFlags.IsRead) };
        await (Task)method.Invoke(_mailService, [pending])!;

        var saved = await _databaseService.Connection.FindAsync<MailCopy>(uniqueId);
        saved.Subject.Should().Be(current.Subject);
        saved.FileId.Should().Be(fileId);
        saved.IsRead.Should().BeTrue();
    }

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

    [Fact]
    public async Task CreateMailAsync_WithSuppressedUiChange_PersistsWithoutMailAddedMessage()
    {
        const string messageId = "filtered-arrival@test.local";
        var existingCopy = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = MailkitClientExtensions.CreateUid(_deletedFolder.Id, 87),
            ImapUid = 87,
            FolderId = _deletedFolder.Id,
            MessageId = messageId,
            FileId = Guid.NewGuid(),
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Filtered arrival",
            CreationDate = DateTime.UtcNow
        };
        await _databaseService.Connection.InsertAsync(existingCopy, typeof(MailCopy));

        var mailCopy = new MailCopy
        {
            Id = MailkitClientExtensions.CreateUid(_inboxFolder.Id, 88),
            ImapUid = 88,
            MessageId = messageId,
            FileId = Guid.NewGuid(),
            FromAddress = "sender@test.local",
            FromName = "Sender",
            Subject = "Filtered arrival",
            CreationDate = DateTime.UtcNow
        };
        var recipient = new MailRetrievalRecipient(mailCopy.Id, existingCopy.Id);
        WeakReferenceMessenger.Default.Register<MailAddedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailAddedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<MailRemovedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailRemovedMessage>(recipient);

        try
        {
            var inserted = await _mailService.CreateMailAsync(
                _account.Id,
                new NewMailItemPackage(
                    mailCopy,
                    null,
                    _inboxFolder.RemoteFolderId,
                    SuppressUiChange: true));

            inserted.Should().BeTrue();
            var persisted = await _databaseService.Connection
                .Table<MailCopy>()
                .FirstOrDefaultAsync(copy => copy.Id == mailCopy.Id);
            persisted.Should().NotBeNull();
            recipient.MatchingUiChangeCount.Should().Be(0);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Draft_identity_updates_preserve_newest_content_and_reject_stale_sync()
    {
        var registry = new DraftUpdateRegistry();
        var service = BuildMailService(_databaseService, registry);
        var draft = new MailCopy
        {
            UniqueId = Guid.NewGuid(), Id = "original", DraftId = "draft", FileId = Guid.NewGuid(),
            IsDraft = true, FolderId = _inboxFolder.Id, AssignedAccount = _account, AssignedFolder = _inboxFolder,
            MessageId = "draft@test.local", Subject = "latest local", FromAddress = _account.Address,
            CreationDate = DateTime.UtcNow
        };
        await _databaseService.Connection.InsertAsync(draft, typeof(MailCopy));
        registry.Protect(_account.Id, draft);
        await service.UpdateDraftIdentityAsync(_account.Id, draft.UniqueId, new("replacement", "draft", "thread", 42, 5));
        await service.MapLocalDraftAsync(_account.Id, draft.UniqueId, "original", "draft", "old-thread");
        await service.DeleteMailAsync(_account.Id, "replacement");
        var saved = await service.GetSingleMailItemAsync(draft.UniqueId);
        saved.Id.Should().Be("replacement"); saved.Subject.Should().Be("latest local");
        saved.FileId.Should().Be(draft.FileId); saved.ImapUid.Should().Be(42);
        registry.Release(_account.Id, draft.UniqueId);
        await service.MapLocalDraftAsync(_account.Id, draft.UniqueId, "original", "draft", "old-thread");
        (await service.GetSingleMailItemAsync(draft.UniqueId)).Id.Should().Be("replacement");
        (await _databaseService.Connection.Table<MailCopy>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Draft_metadata_save_cannot_restore_an_obsolete_remote_id()
    {
        var registry = new DraftUpdateRegistry();
        var service = BuildMailService(_databaseService, registry);
        var draft = new MailCopy
        {
            UniqueId = Guid.NewGuid(), Id = "original", DraftId = "draft", FileId = Guid.NewGuid(),
            IsDraft = true, FolderId = _inboxFolder.Id, AssignedAccount = _account, AssignedFolder = _inboxFolder,
            Subject = "old", FromAddress = _account.Address, CreationDate = DateTime.UtcNow
        };
        await _databaseService.Connection.InsertAsync(draft, typeof(MailCopy));
        await service.UpdateDraftIdentityAsync(_account.Id, draft.UniqueId, new("replacement", "draft", "thread"));
        draft.Subject = "new local subject";
        await service.SaveDraftMetadataAsync(_account.Id, draft);
        var saved = await service.GetSingleMailItemAsync(draft.UniqueId);
        saved.Id.Should().Be("replacement"); saved.Subject.Should().Be("new local subject");
    }

    private static MailService BuildMailService(InMemoryDatabaseService db, DraftUpdateRegistry? registry = null)
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
            mailCategoryService, draftUpdates: registry);
    }

    public sealed class MailRetrievalRecipient(params string[] targetMailIds) :
        IRecipient<MailAddedMessage>,
        IRecipient<BulkMailAddedMessage>,
        IRecipient<MailRemovedMessage>,
        IRecipient<BulkMailRemovedMessage>
    {
        private readonly HashSet<string> _targetMailIds = targetMailIds.ToHashSet(StringComparer.Ordinal);

        public int MatchingUiChangeCount { get; private set; }

        public void Receive(MailAddedMessage message)
        {
            if (message.AddedMail != null && _targetMailIds.Contains(message.AddedMail.Id))
                MatchingUiChangeCount++;
        }

        public void Receive(BulkMailAddedMessage message)
        {
            MatchingUiChangeCount += message.AddedMails.Count(
                mail => mail != null && _targetMailIds.Contains(mail.Id));
        }

        public void Receive(MailRemovedMessage message)
        {
            if (message.RemovedMail != null && _targetMailIds.Contains(message.RemovedMail.Id))
                MatchingUiChangeCount++;
        }

        public void Receive(BulkMailRemovedMessage message)
        {
            MatchingUiChangeCount += message.RemovedMails.Count(
                mail => mail != null && _targetMailIds.Contains(mail.Id));
        }
    }
}
