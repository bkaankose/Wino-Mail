using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Tests.Helpers;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailThreadingTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private MailService _mailService = null!;
    private MailAccount _account = null!;
    private MailItemFolder _draftFolder = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Threading Test Account",
            Address = "me@test.local",
            SenderName = "Test User",
            ProviderType = MailProviderType.IMAP4
        };

        _draftFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Drafts",
            SpecialFolderType = SpecialFolderType.Draft,
            IsSystemFolder = true,
            IsSynchronizationEnabled = true
        };

        var preferences = new MailAccountPreferences
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            IsNotificationsEnabled = true,
            IsSignatureEnabled = false
        };

        var alias = new MailAccountAlias
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            AliasAddress = _account.Address,
            ReplyToAddress = _account.Address,
            IsPrimary = true,
            IsRootAlias = true,
            IsVerified = true,
            Source = AliasSource.Manual,
            SendCapability = AliasSendCapability.Confirmed
        };

        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));
        await _databaseService.Connection.InsertAsync(_draftFolder, typeof(MailItemFolder));
        await _databaseService.Connection.InsertAsync(preferences, typeof(MailAccountPreferences));
        await _databaseService.Connection.InsertAsync(alias, typeof(MailAccountAlias));

        _mailService = BuildMailService(_databaseService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task CreateDraftAsync_EmptyDraft_AssignsGeneratedMessageId()
    {
        var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions { Reason = DraftCreationReason.Empty });

        var mimeMessage = draftBase64MimeMessage.GetMimeMessageFromBase64();

        draftMailCopy.MessageId.Should().MatchRegex("^[0-9a-fA-F-]{36}@wino-mail\\.app$");
        mimeMessage.MessageId.Should().Be(draftMailCopy.MessageId);
        mimeMessage.Headers[HeaderId.MessageId].Should().Be(MailHeaderExtensions.ToHeaderMessageId(draftMailCopy.MessageId));
        draftMailCopy.DraftSyncState.Should().Be(DraftSyncState.PendingSync);
        draftMailCopy.SenderContact.Should().NotBeNull();
        draftMailCopy.SenderContact.Address.Should().Be(_account.Address);
        draftMailCopy.SenderContact.Name.Should().Be(_account.SenderName);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenMimeSaveFails_ThrowsWithoutCreatingRowOrMessage()
    {
        var mailService = BuildMailService(_databaseService, saveMimeSuccessfully: false);
        var recipient = new DraftCreatedRecipient();
        WeakReferenceMessenger.Default.Register<DraftCreated>(recipient);

        try
        {
            var act = () => mailService.CreateDraftAsync(
                _account.Id,
                new DraftCreationOptions { Reason = DraftCreationReason.Empty });

            await act.Should().ThrowAsync<MimePersistenceException>();
            (await _databaseService.Connection.Table<MailCopy>().CountAsync()).Should().Be(0);
            recipient.Messages.Should().BeEmpty();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task DraftSyncTransitions_FailurePublishesOnce_AndMappingResetsState()
    {
        var (draft, _) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions { Reason = DraftCreationReason.Empty });

        var recipient = new DraftFailedRecipient();
        WeakReferenceMessenger.Default.Register<DraftFailed>(recipient);

        try
        {
            await _mailService.MarkDraftSyncAttemptAsync(draft.UniqueId);
            await _mailService.MarkDraftSyncFailedAsync(draft.UniqueId, "network unavailable");
            await _mailService.MarkDraftSyncFailedAsync(draft.UniqueId, "duplicate safety-net failure");

            recipient.Messages.Should().ContainSingle();

            var mapped = await _mailService.MapLocalDraftAsync(
                _account.Id,
                draft.UniqueId,
                "remote-id",
                "remote-draft-id",
                "remote-thread-id");

            mapped.Should().BeTrue();
            var stored = await _databaseService.Connection.FindAsync<MailCopy>(draft.UniqueId);
            stored.DraftSyncState.Should().Be(DraftSyncState.Synced);
            stored.DraftSyncAttemptCount.Should().Be(0);
            stored.LastDraftSyncError.Should().BeNull();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_Reply_SetsInReplyToReferencesAndReplySubject()
    {
        const string parentMessageId = "original@domain.com";

        var referencedMimeMessage = CreateReferencedMimeMessage("From outlook", parentMessageId);
        var referencedMailCopy = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            ThreadId = "provider-thread-id",
            MessageId = parentMessageId
        };

        var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = referencedMimeMessage,
                    MailCopy = referencedMailCopy
                }
            });

        var mimeMessage = draftBase64MimeMessage.GetMimeMessageFromBase64();

        draftMailCopy.InReplyTo.Should().Be(parentMessageId);
        draftMailCopy.References.Should().Be(parentMessageId);
        draftMailCopy.Subject.Should().Be("Re: From outlook");
        draftMailCopy.ThreadId.Should().Be(referencedMailCopy.ThreadId);

        mimeMessage.InReplyTo.Should().Be(parentMessageId);
        MailHeaderExtensions.NormalizeReferences(mimeMessage.Headers[HeaderId.References]).Should().Be(parentMessageId);
    }

    [Fact]
    public async Task CreateDraftAsync_Reply_EncodesPlainTextPrefillAboveQuotedMessage()
    {
        var referencedMimeMessage = CreateReferencedMimeMessage("Original subject", "original@domain.com");
        referencedMimeMessage.Body = new TextPart("html") { Text = "<p>Quoted body</p>" };

        var (_, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                InitialBodyText = "First <line> & value\nSecond line",
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = referencedMimeMessage,
                    MailCopy = new MailCopy
                    {
                        UniqueId = Guid.NewGuid(),
                        Id = Guid.NewGuid().ToString(),
                        MessageId = "original@domain.com"
                    }
                }
            });

        var html = draftBase64MimeMessage.GetMimeMessageFromBase64().HtmlBody;

        html.Should().Contain("First &lt;line&gt; &amp; value<br>Second line");
        html.IndexOf("First &lt;line&gt;", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Quoted body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDraftAsync_Reply_AppendsReferencesChainOnce()
    {
        const string rootMessageId = "root@domain.com";
        const string middleMessageId = "middle@domain.com";
        const string parentMessageId = "parent@domain.com";

        var referencedMimeMessage = CreateReferencedMimeMessage("Re: Existing subject", parentMessageId);
        referencedMimeMessage.References.Add(rootMessageId);
        referencedMimeMessage.References.Add(middleMessageId);

        var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = referencedMimeMessage,
                    MailCopy = new MailCopy { UniqueId = Guid.NewGuid(), Id = Guid.NewGuid().ToString(), MessageId = parentMessageId }
                }
            });

        var mimeMessage = draftBase64MimeMessage.GetMimeMessageFromBase64();

        draftMailCopy.References.Should().Be($"{rootMessageId};{middleMessageId};{parentMessageId}");
        draftMailCopy.Subject.Should().Be("Re: Existing subject");
        MailHeaderExtensions.NormalizeReferences(mimeMessage.Headers[HeaderId.References])
            .Should().Be($"{rootMessageId};{middleMessageId};{parentMessageId}");
    }

    [Fact]
    public async Task CreateDraftAsync_Reply_FallsBackToReferencedMailCopyThreadingMetadata()
    {
        const string rootMessageId = "root@domain.com";
        const string parentMessageId = "copy-parent@domain.com";

        var referencedMimeMessage = CreateReferencedMimeMessage("Fallback subject");
        referencedMimeMessage.Headers.Remove(HeaderId.MessageId);

        var referencedMailCopy = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            MessageId = parentMessageId,
            References = rootMessageId
        };

        var (draftMailCopy, _) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = referencedMimeMessage,
                    MailCopy = referencedMailCopy
                }
            });

        draftMailCopy.InReplyTo.Should().Be(parentMessageId);
        draftMailCopy.References.Should().Be($"{rootMessageId};{parentMessageId}");
    }

    [Fact]
    public async Task CreateDraftAsync_Reply_PicksAliasFromDeliveredToHeader()
    {
        var secondaryAlias = new MailAccountAlias
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            AliasAddress = "support@test.local",
            ReplyToAddress = "support@test.local",
            IsPrimary = false,
            IsRootAlias = false,
            Source = AliasSource.Manual,
            SendCapability = AliasSendCapability.Unknown
        };

        await _databaseService.Connection.InsertAsync(secondaryAlias, typeof(MailAccountAlias));

        var referencedMimeMessage = CreateReferencedMimeMessage("Alias reply");
        referencedMimeMessage.Headers.Add("Delivered-To", "<support@test.local>");

        var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(
            _account.Id,
            new DraftCreationOptions
            {
                Reason = DraftCreationReason.Reply,
                ReferencedMessage = new ReferencedMessage
                {
                    MimeMessage = referencedMimeMessage,
                    MailCopy = new MailCopy { UniqueId = Guid.NewGuid(), Id = Guid.NewGuid().ToString(), MessageId = "alias-parent@domain.com" }
                }
            });

        var mimeMessage = draftBase64MimeMessage.GetMimeMessageFromBase64();

        draftMailCopy.FromAddress.Should().Be("support@test.local");
        mimeMessage.From.Mailboxes.Should().ContainSingle(m => m.Address == "support@test.local");
        mimeMessage.ReplyTo.Mailboxes.Should().ContainSingle(m => m.Address == "support@test.local");
    }

    [Fact]
    public async Task ApplyMailStateUpdatesAsync_ForBatchReadStateChange_SendsBulkMailUpdatedMessage()
    {
        var mail1 = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsRead = true,
            Subject = "First"
        };
        var mail2 = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsRead = true,
            Subject = "Second"
        };

        await _databaseService.Connection.InsertAllAsync(new[] { mail1, mail2 }, typeof(MailCopy));

        var recipient = new MailUpdateRecipient();
        WeakReferenceMessenger.Default.Register<MailUpdatedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailUpdatedMessage>(recipient);

        try
        {
            await _mailService.ApplyMailStateUpdatesAsync(
            [
                new MailCopyStateUpdate(mail1.Id, IsRead: false),
                new MailCopyStateUpdate(mail2.Id, IsRead: false)
            ]);

            recipient.SingleUpdates.Should().BeEmpty();
            recipient.BulkUpdates.Should().ContainSingle();
            recipient.BulkUpdates[0].Source.Should().Be(EntityUpdateSource.Server);
            recipient.BulkUpdates[0].ChangedProperties.Should().Be(MailCopyChangeFlags.IsRead);
            recipient.BulkUpdates[0].UpdatedMails.Should().HaveCount(2);
            recipient.BulkUpdates[0].UpdatedMails.Should().OnlyContain(x => !x.IsRead);

            (await _databaseService.Connection.FindAsync<MailCopy>(mail1.UniqueId))!.IsRead.Should().BeFalse();
            (await _databaseService.Connection.FindAsync<MailCopy>(mail2.UniqueId))!.IsRead.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<MailUpdatedMessage>(recipient);
            WeakReferenceMessenger.Default.Unregister<BulkMailUpdatedMessage>(recipient);
        }
    }

    [Fact]
    public async Task ApplyMailStateUpdatesAsync_ForFocusedStateChange_SendsBulkMailUpdatedMessage()
    {
        var mail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsFocused = true,
            Subject = "Focused mail"
        };

        await _databaseService.Connection.InsertAsync(mail, typeof(MailCopy));

        var recipient = new MailUpdateRecipient();
        WeakReferenceMessenger.Default.Register<BulkMailUpdatedMessage>(recipient);

        try
        {
            await _mailService.ApplyMailStateUpdatesAsync(
                [new MailCopyStateUpdate(mail.Id, IsFocused: false)]);

            recipient.BulkUpdates.Should().ContainSingle();
            recipient.BulkUpdates[0].ChangedProperties.Should().Be(MailCopyChangeFlags.IsFocused);
            recipient.BulkUpdates[0].UpdatedMails.Should().ContainSingle();
            recipient.BulkUpdates[0].UpdatedMails[0].IsFocused.Should().BeFalse();
            (await _databaseService.Connection.FindAsync<MailCopy>(mail.UniqueId))!.IsFocused.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<BulkMailUpdatedMessage>(recipient);
        }
    }

    [Fact]
    public async Task ApplyMailStateUpdatesAsync_ForBatchMarkRead_SendsBulkMailReadStatusChanged()
    {
        var mail1 = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsRead = false,
            Subject = "First unread"
        };
        var mail2 = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsRead = false,
            Subject = "Second unread"
        };

        await _databaseService.Connection.InsertAllAsync(new[] { mail1, mail2 }, typeof(MailCopy));

        var recipient = new MailReadStatusRecipient();
        WeakReferenceMessenger.Default.Register<MailReadStatusChanged>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailReadStatusChanged>(recipient);

        try
        {
            await _mailService.ApplyMailStateUpdatesAsync(
            [
                new MailCopyStateUpdate(mail1.Id, IsRead: true),
                new MailCopyStateUpdate(mail2.Id, IsRead: true)
            ]);

            recipient.SingleUpdates.Should().BeEmpty();
            recipient.BulkUpdates.Should().ContainSingle();
            recipient.BulkUpdates[0].UniqueIds.Should().BeEquivalentTo([mail1.UniqueId, mail2.UniqueId]);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<MailReadStatusChanged>(recipient);
            WeakReferenceMessenger.Default.Unregister<BulkMailReadStatusChanged>(recipient);
        }
    }

    [Fact]
    public async Task ChangePinnedStatusAsync_SendsHydratedBulkMailUpdatedMessage()
    {
        var mail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = _draftFolder.Id,
            IsPinned = false,
            Subject = "Pinned draft"
        };

        await _databaseService.Connection.InsertAsync(mail, typeof(MailCopy));

        var recipient = new MailUpdateRecipient();
        WeakReferenceMessenger.Default.Register<MailUpdatedMessage>(recipient);
        WeakReferenceMessenger.Default.Register<BulkMailUpdatedMessage>(recipient);

        try
        {
            await _mailService.ChangePinnedStatusAsync([mail.UniqueId], true);

            recipient.SingleUpdates.Should().BeEmpty();
            recipient.BulkUpdates.Should().ContainSingle();
            recipient.BulkUpdates[0].ChangedProperties.Should().Be(MailCopyChangeFlags.IsPinned);
            recipient.BulkUpdates[0].UpdatedMails.Should().ContainSingle();

            var updatedMail = recipient.BulkUpdates[0].UpdatedMails[0];
            updatedMail.IsPinned.Should().BeTrue();
            updatedMail.AssignedFolder.Should().NotBeNull();
            updatedMail.AssignedFolder!.Id.Should().Be(_draftFolder.Id);
            updatedMail.AssignedAccount.Should().NotBeNull();
            updatedMail.AssignedAccount!.Id.Should().Be(_account.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<MailUpdatedMessage>(recipient);
            WeakReferenceMessenger.Default.Unregister<BulkMailUpdatedMessage>(recipient);
        }
    }

    [Fact]
    public async Task CreateAssignmentAsync_SendsHydratedMailAddedMessage()
    {
        var archiveFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Archive",
            RemoteFolderId = "archive",
            SpecialFolderType = SpecialFolderType.Archive,
            IsSystemFolder = true,
            IsSynchronizationEnabled = true
        };

        var mail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "assignment-mail",
            FolderId = _draftFolder.Id,
            Subject = "Assigned copy"
        };

        await _databaseService.Connection.InsertAsync(archiveFolder, typeof(MailItemFolder));
        await _databaseService.Connection.InsertAsync(mail, typeof(MailCopy));

        var recipient = new MailAddRecipient();
        WeakReferenceMessenger.Default.Register<MailAddedMessage>(recipient);

        try
        {
            await _mailService.CreateAssignmentAsync(_account.Id, mail.Id, archiveFolder.RemoteFolderId);

            recipient.Added.Should().ContainSingle();

            var addedMail = recipient.Added[0].AddedMail;
            addedMail.UniqueId.Should().NotBe(mail.UniqueId);
            addedMail.AssignedFolder.Should().NotBeNull();
            addedMail.AssignedFolder!.Id.Should().Be(archiveFolder.Id);
            addedMail.AssignedAccount.Should().NotBeNull();
            addedMail.AssignedAccount!.Id.Should().Be(_account.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<MailAddedMessage>(recipient);
        }
    }

    private static MimeMessage CreateReferencedMimeMessage(string subject, string? messageId = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "Body" };

        if (!string.IsNullOrWhiteSpace(messageId))
            message.MessageId = messageId;

        return message;
    }

    internal sealed class MailUpdateRecipient : IRecipient<MailUpdatedMessage>, IRecipient<BulkMailUpdatedMessage>
    {
        public List<MailUpdatedMessage> SingleUpdates { get; } = [];
        public List<BulkMailUpdatedMessage> BulkUpdates { get; } = [];

        public void Receive(MailUpdatedMessage message) => SingleUpdates.Add(message);
        public void Receive(BulkMailUpdatedMessage message) => BulkUpdates.Add(message);
    }

    internal sealed class MailAddRecipient : IRecipient<MailAddedMessage>
    {
        public List<MailAddedMessage> Added { get; } = [];

        public void Receive(MailAddedMessage message) => Added.Add(message);
    }

    internal sealed class MailReadStatusRecipient : IRecipient<MailReadStatusChanged>, IRecipient<BulkMailReadStatusChanged>
    {
        public List<MailReadStatusChanged> SingleUpdates { get; } = [];
        public List<BulkMailReadStatusChanged> BulkUpdates { get; } = [];

        public void Receive(MailReadStatusChanged message) => SingleUpdates.Add(message);
        public void Receive(BulkMailReadStatusChanged message) => BulkUpdates.Add(message);
    }

    internal sealed class DraftCreatedRecipient : IRecipient<DraftCreated>
    {
        public List<DraftCreated> Messages { get; } = [];
        public void Receive(DraftCreated message) => Messages.Add(message);
    }

    internal sealed class DraftFailedRecipient : IRecipient<DraftFailed>
    {
        public List<DraftFailed> Messages { get; } = [];
        public void Receive(DraftFailed message) => Messages.Add(message);
    }

    private static MailService BuildMailService(InMemoryDatabaseService db, bool saveMimeSuccessfully = true)
    {
        var signatureService = new Mock<ISignatureService>();
        var authProvider = new Mock<IAuthenticationProvider>();
        var mimeFileService = new Mock<IMimeFileService>();
        mimeFileService
            .Setup(x => x.SaveMimeMessageAsync(It.IsAny<Guid>(), It.IsAny<MimeMessage>(), It.IsAny<Guid>()))
            .ReturnsAsync(saveMimeSuccessfully);
        mimeFileService
            .Setup(x => x.CreateHTMLPreviewVisitor(It.IsAny<MimeMessage>(), It.IsAny<string>()))
            .Returns<MimeMessage, string>((_, _) => new HtmlPreviewVisitor(string.Empty));

        var preferencesService = new Mock<IPreferencesService>();
        preferencesService.SetupProperty(x => x.ComposerFont, "Calibri");
        preferencesService.SetupProperty(x => x.ComposerFontSize, 12);

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
