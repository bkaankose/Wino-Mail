using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers.Mail;
using Wino.Core.Requests.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class Pop3SynchronizerTests
{
    [Fact]
    public async Task Synchronize_requires_uidl()
    {
        var client = new FakePop3Client { SupportsUids = false };
        var fixture = CreateFixture(client);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Failed);
        fixture.MailService.Verify(service => service.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>()), Times.Never);
    }

    [Fact]
    public async Task Initial_import_filters_old_headers_and_saves_full_mime_for_selected_messages()
    {
        var client = new FakePop3Client
        {
            Uidls = ["old", "new"],
            Headers =
            {
                [0] = CreateHeaders(DateTimeOffset.UtcNow.AddYears(-2)),
                [1] = CreateHeaders(DateTimeOffset.UtcNow.AddDays(-1))
            },
            Messages = { [1] = CreateMessage("new@example.test") }
        };
        var fixture = CreateFixture(client);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        client.RequestedMessageIndexes.Should().Equal(1);
        fixture.MailService.Verify(service => service.CreateMailAsync(
            fixture.Account.Id,
            It.Is<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>(package => package.Copy.Pop3Uidl == "new" && package.Mime != null)), Times.Once);
    }

    [Fact]
    public async Task Incremental_import_uses_uidl_identity_after_reorder()
    {
        var client = new FakePop3Client
        {
            Uidls = ["uid-b", "uid-a", "uid-c"],
            Messages = { [2] = CreateMessage("c@example.test") }
        };
        var fixture = CreateFixture(client, knownUidls: ["uid-a", "uid-b"], initialImportComplete: true);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        client.RequestedMessageIndexes.Should().Equal(2);
        fixture.MailService.Verify(service => service.CreateMailAsync(
            fixture.Account.Id,
            It.Is<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>(package => package.Copy.Pop3Uidl == "uid-c")), Times.Once);
    }

    [Fact]
    public async Task Pending_deletion_is_committed_and_cleared_only_after_clean_disconnect()
    {
        var tombstone = new Pop3PendingServerDeletion
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Uidl = "delete-me"
        };
        var client = new FakePop3Client { Uidls = ["keep", "delete-me"] };
        var fixture = CreateFixture(client, pendingDeletions: [tombstone], knownUidls: ["keep"], initialImportComplete: true);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        client.DeletedMessageIndexes.Should().Equal(1);
        client.DisconnectCommits.Should().ContainSingle().Which.Should().BeTrue();
        fixture.Persistence.Verify(service => service.RemovePendingDeletionsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { tombstone.Id }))), Times.Once);
    }

    [Fact]
    public async Task Failed_commit_keeps_tombstone_for_retry_and_rolls_back_session()
    {
        var tombstone = new Pop3PendingServerDeletion
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Uidl = "delete-me"
        };
        var client = new FakePop3Client
        {
            Uidls = ["delete-me"],
            FailCommitDisconnect = true
        };
        var fixture = CreateFixture(client, pendingDeletions: [tombstone], initialImportComplete: true);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Failed);
        fixture.Persistence.Verify(service => service.RemovePendingDeletionsAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
        fixture.Persistence.Verify(service => service.MarkDeletionAttemptFailedAsync(tombstone.Id, It.IsAny<string>()), Times.Once);
        client.DisconnectCommits.Should().Equal(true, false);
    }

    [Fact]
    public async Task Remote_disappearance_never_deletes_local_mail()
    {
        var client = new FakePop3Client { Uidls = [] };
        var fixture = CreateFixture(client, knownUidls: ["remote-is-gone"], initialImportComplete: true);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        fixture.MailService.Verify(service => service.DeleteMailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Permanent_delete_persists_uidl_tombstone_before_removing_local_copy()
    {
        var client = new FakePop3Client { Uidls = [] };
        var fixture = CreateFixture(client, initialImportComplete: true);
        var mail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "pop3-mail",
            Pop3Uidl = "stable-uidl",
            FolderId = Guid.NewGuid()
        };
        fixture.Synchronizer.QueueRequest(new DeleteRequest(mail));

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = fixture.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        fixture.Persistence.Verify(service => service.AddPendingDeletionAsync(fixture.Account.Id, "stable-uidl"), Times.Once);
        fixture.MailService.Verify(service => service.DeleteMailAsync(fixture.Account.Id, mail.Id), Times.Once);
    }

    [Fact]
    public async Task Read_and_flag_mutations_are_persisted_locally()
    {
        var fixture = CreateFixture(new FakePop3Client { Uidls = [] }, initialImportComplete: true);
        var mail = new MailCopy { UniqueId = Guid.NewGuid(), Id = "pop3-mail", FolderId = Guid.NewGuid() };
        fixture.Synchronizer.QueueRequest(new MarkReadRequest(mail, true));
        fixture.Synchronizer.QueueRequest(new ChangeFlagRequest(mail, true));

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = fixture.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        fixture.MailService.Verify(service => service.ChangeReadStatusAsync(mail.Id, true), Times.Once);
        fixture.MailService.Verify(service => service.ChangeFlagStatusAsync(mail.Id, true), Times.Once);
    }

    [Fact]
    public async Task Accepted_smtp_draft_is_saved_to_local_sent_before_local_draft_is_removed()
    {
        var fixture = CreateFixture(new FakePop3Client { Uidls = [] }, initialImportComplete: true);
        var draftFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = fixture.Account.Id,
            RemoteFolderId = "local-drafts",
            SpecialFolderType = SpecialFolderType.Draft
        };
        var sentFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = fixture.Account.Id,
            RemoteFolderId = "local-sent",
            SpecialFolderType = SpecialFolderType.Sent
        };
        var draft = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "local-draft",
            FolderId = draftFolder.Id,
            AssignedFolder = draftFolder
        };
        var accepted = CreateMessage("accepted@example.test");
        var operations = new List<string>();
        fixture.SmtpTransport
            .Setup(transport => transport.SendAsync(fixture.Account, It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("smtp"))
            .ReturnsAsync(accepted);
        fixture.MailService
            .Setup(service => service.CreateMailRawAsync(
                fixture.Account,
                sentFolder,
                It.Is<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>(package => package.Copy.IsRead)))
            .Callback(() => operations.Add("sent"))
            .Returns(Task.CompletedTask);
        fixture.MailService
            .Setup(service => service.DeleteMailAsync(fixture.Account.Id, draft.Id))
            .Callback(() => operations.Add("delete"))
            .Returns(Task.CompletedTask);
        fixture.Synchronizer.QueueRequest(new SendDraftRequest(new SendDraftPreparationRequest(
            draft,
            null,
            sentFolder,
            draftFolder,
            new MailAccountPreferences(),
            accepted.GetBase64MimeMessage())));

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = fixture.Account.Id,
            Type = MailSynchronizationType.ExecuteRequests
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        operations.Should().Equal("smtp", "sent", "delete");
    }

    [Fact]
    public async Task Duplicate_uidls_fail_without_importing_ambiguous_messages()
    {
        var fixture = CreateFixture(new FakePop3Client { Uidls = ["duplicate", "duplicate"] });

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Failed);
        fixture.MailService.Verify(service => service.CreateMailAsync(
            It.IsAny<Guid>(), It.IsAny<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>()), Times.Never);
    }

    [Fact]
    public async Task Partial_mime_failure_does_not_block_other_uidls_and_is_retried_later()
    {
        var client = new FakePop3Client
        {
            Uidls = ["broken", "healthy"],
            FailMessageIndexes = { 0 },
            Messages = { [1] = CreateMessage("healthy@example.test") }
        };
        var fixture = CreateFixture(client, initialImportComplete: true);

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.PartiallyCompleted);
        client.RequestedMessageIndexes.Should().Equal(0, 1);
        fixture.MailService.Verify(service => service.CreateMailAsync(
            fixture.Account.Id,
            It.Is<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>(package => package.Copy.Pop3Uidl == "healthy")), Times.Once);
        fixture.Persistence.Verify(service => service.MarkUidlKnownAsync(fixture.Account.Id, "broken"), Times.Never);
    }

    [Fact]
    public async Task Cancellation_rolls_back_pending_server_deletions()
    {
        var client = new FakePop3Client { Uidls = ["uid"] };
        var fixture = CreateFixture(client, initialImportComplete: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.Synchronizer.SynchronizeMailsAsync(CreateOptions(), cancellation.Token);

        result.CompletedState.Should().Be(SynchronizationCompletedState.Canceled);
        client.DisconnectCommits.Should().NotContain(true);
    }

    private static Fixture CreateFixture(
        FakePop3Client client,
        IEnumerable<string>? knownUidls = null,
        IReadOnlyList<Pop3PendingServerDeletion>? pendingDeletions = null,
        bool initialImportComplete = false)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "POP account",
            Address = "user@example.test",
            ProviderType = MailProviderType.POP3,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths,
            SynchronizationDeltaIdentifier = initialImportComplete ? "pop3-uidl-v1" : string.Empty,
            ServerInformation = new CustomServerInformation
            {
                AccountId = Guid.NewGuid(),
                IncomingServer = "pop.example.test",
                IncomingServerPort = "995"
            }
        };
        var inbox = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            RemoteFolderId = "local-inbox",
            FolderName = "Inbox",
            SpecialFolderType = SpecialFolderType.Inbox
        };
        var factory = new Mock<IPop3ClientFactory>();
        factory.Setup(value => value.Create(account.Id, false)).Returns(client);
        var persistence = new Mock<IPop3PersistenceService>();
        persistence.Setup(service => service.GetKnownUidlsAsync(account.Id))
            .ReturnsAsync((knownUidls ?? []).ToHashSet(StringComparer.Ordinal));
        persistence.Setup(service => service.GetPendingDeletionsAsync(account.Id))
            .ReturnsAsync(pendingDeletions ?? []);
        var mailService = new Mock<IMailService>();
        mailService.Setup(service => service.CreateMailAsync(account.Id, It.IsAny<Wino.Core.Domain.Models.MailItem.NewMailItemPackage>()))
            .ReturnsAsync(true);
        var folderService = new Mock<IFolderService>();
        folderService.Setup(service => service.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox))
            .ReturnsAsync(inbox);
        var accountService = new Mock<IAccountService>();
        var smtpTransport = new Mock<ISmtpTransport>();
        var mimeFileService = new Mock<IMimeFileService>();
        mimeFileService.Setup(service => service.SaveMimeMessageAsync(It.IsAny<Guid>(), It.IsAny<MimeMessage>(), account.Id))
            .ReturnsAsync(true);
        var synchronizer = new Pop3Synchronizer(
            account,
            factory.Object,
            persistence.Object,
            mailService.Object,
            folderService.Object,
            accountService.Object,
            smtpTransport.Object,
            mimeFileService.Object);

        return new Fixture(synchronizer, account, persistence, mailService, smtpTransport);
    }

    private static MailSynchronizationOptions CreateOptions() => new()
    {
        AccountId = Guid.NewGuid(),
        Type = MailSynchronizationType.FullFolders
    };

    private static HeaderList CreateHeaders(DateTimeOffset date)
    {
        var headers = new HeaderList();
        headers.Add(HeaderId.Date, date.ToString("r"));
        return headers;
    }

    private static MimeMessage CreateMessage(string messageId)
    {
        var message = new MimeMessage
        {
            MessageId = messageId,
            Subject = "Test",
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = "Message body" }
        };
        message.From.Add(MailboxAddress.Parse("Sender <sender@example.test>"));
        message.To.Add(MailboxAddress.Parse("user@example.test"));
        return message;
    }

    private sealed record Fixture(
        Pop3Synchronizer Synchronizer,
        MailAccount Account,
        Mock<IPop3PersistenceService> Persistence,
        Mock<IMailService> MailService,
        Mock<ISmtpTransport> SmtpTransport);

    private sealed class FakePop3Client : IPop3ClientAdapter
    {
        public bool IsConnected { get; private set; }
        public bool IsAuthenticated { get; private set; }
        public bool SupportsUids { get; set; } = true;
        public int Count => Uidls.Count;
        public List<string> Uidls { get; init; } = [];
        public Dictionary<int, HeaderList> Headers { get; } = [];
        public Dictionary<int, MimeMessage> Messages { get; } = [];
        public List<int> RequestedMessageIndexes { get; } = [];
        public List<int> DeletedMessageIndexes { get; } = [];
        public List<bool> DisconnectCommits { get; } = [];
        public bool FailCommitDisconnect { get; init; }
        public HashSet<int> FailMessageIndexes { get; } = [];

        public Task ConnectAndAuthenticateAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            IsAuthenticated = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Uidls);

        public Task<HeaderList> GetMessageHeadersAsync(int index, CancellationToken cancellationToken = default)
            => Task.FromResult(Headers.GetValueOrDefault(index) ?? CreateHeaders(DateTimeOffset.UtcNow));

        public Task<MimeMessage> GetMessageAsync(int index, CancellationToken cancellationToken = default)
        {
            RequestedMessageIndexes.Add(index);
            if (FailMessageIndexes.Contains(index))
                throw new IOException("MIME retrieval failed.");
            return Task.FromResult(Messages.GetValueOrDefault(index) ?? CreateMessage($"message-{index}@example.test"));
        }

        public Task DeleteMessageAsync(int index, CancellationToken cancellationToken = default)
        {
            DeletedMessageIndexes.Add(index);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool commitDeletions, CancellationToken cancellationToken = default)
        {
            DisconnectCommits.Add(commitDeletions);
            if (commitDeletions && FailCommitDisconnect)
                throw new IOException("Commit failed.");

            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
