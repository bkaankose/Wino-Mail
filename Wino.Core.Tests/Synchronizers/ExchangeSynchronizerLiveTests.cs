using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Exchange;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Live test against an on-premises Exchange mailbox over EWS (NTLM credentials).
/// Configure via environment variables to run; skipped automatically when absent:
///   EXCHANGE_EWS_URL, EXCHANGE_EMAIL, EXCHANGE_PASSWORD
/// The change processor is mocked with in-memory state (mirrors ImapSynchronizerLiveTests).
/// </summary>
public sealed class ExchangeSynchronizerLiveTests
{
    private static (string Url, string Email, string Password)? ReadCredentials()
    {
        var url = Environment.GetEnvironmentVariable("EXCHANGE_EWS_URL");
        var email = Environment.GetEnvironmentVariable("EXCHANGE_EMAIL");
        var password = Environment.GetEnvironmentVariable("EXCHANGE_PASSWORD");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        return (url, email, password);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task FullSync_DiscoversFolders_AndDownloadsInbox()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return; // not configured — skip

        var context = CreateContext(credentials.Value);

        var result = await context.Synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions
        {
            AccountId = context.Account.Id,
            Type = MailSynchronizationType.FullFolders
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        context.Folders.Should().Contain(f => f.SpecialFolderType == SpecialFolderType.Inbox, "folder sync should discover the Inbox");
        context.StoredMails.Values.Should().NotBeEmpty("the test mailbox has at least one message in the Inbox");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DeltaSync_SecondRun_DownloadsNoNewMail()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return;

        var context = CreateContext(credentials.Value);
        var options = new MailSynchronizationOptions { AccountId = context.Account.Id, Type = MailSynchronizationType.FullFolders };

        var first = await context.Synchronizer.SynchronizeMailsAsync(options);
        first.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        var afterFirst = context.StoredMails.Count;

        var second = await context.Synchronizer.SynchronizeMailsAsync(options);
        second.CompletedState.Should().Be(SynchronizationCompletedState.Success);

        // Per-folder DeltaToken should make the second pass a no-op (no duplicate inserts).
        context.StoredMails.Count.Should().Be(afterFirst);
        second.DownloadedMessages.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task MarkRead_RoundTrips_ThroughServerAndDelta()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return;

        var context = CreateContext(credentials.Value);
        var options = new MailSynchronizationOptions { AccountId = context.Account.Id, Type = MailSynchronizationType.FullFolders };

        await context.Synchronizer.SynchronizeMailsAsync(options);
        var target = context.StoredMails.Values.First();

        await ExecuteMarkReadAsync(context.Synchronizer, target, isRead: false);
        await context.Synchronizer.SynchronizeMailsAsync(options);
        context.StoredMails[target.Id].IsRead.Should().BeFalse("server read-flag change should flow back via delta");

        await ExecuteMarkReadAsync(context.Synchronizer, target, isRead: true);
        await context.Synchronizer.SynchronizeMailsAsync(options);
        context.StoredMails[target.Id].IsRead.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task SendDraft_SendsAndSavesToSentFolder()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return;

        var context = CreateContext(credentials.Value);
        var options = new MailSynchronizationOptions { AccountId = context.Account.Id, Type = MailSynchronizationType.FullFolders };

        await context.Synchronizer.SynchronizeMailsAsync(options);
        var sentFolder = context.Folders.First(f => f.SpecialFolderType == SpecialFolderType.Sent);

        var subject = "Exora EWS live send test " + Guid.NewGuid().ToString("N");
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(credentials.Value.Email));
        mime.To.Add(MailboxAddress.Parse(credentials.Value.Email));
        mime.Subject = subject;
        mime.Body = new TextPart("plain") { Text = "Exora EWS SendDraft live test." };

        var preparation = new SendDraftPreparationRequest(
            MailItem: new MailCopy { Id = Guid.NewGuid().ToString(), UniqueId = Guid.NewGuid(), AssignedFolder = sentFolder },
            SendingAlias: null,
            SentFolder: sentFolder,
            DraftFolder: null,
            AccountPreferences: new MailAccountPreferences { ShouldAppendMessagesToSentFolder = true },
            Base64MimeMessage: mime.GetBase64MimeMessage());

        var bundles = context.Synchronizer.SendDraft(new SendDraftRequest(preparation));
        await context.Synchronizer.ExecuteNativeRequestsAsync(bundles);

        await context.Synchronizer.SynchronizeMailsAsync(options);
        context.StoredMails.Values.Should().Contain(m => m.Subject == subject, "the sent copy should land in Sent Items");
    }

    private static async Task ExecuteMarkReadAsync(ExchangeSynchronizer synchronizer, MailCopy mail, bool isRead)
    {
        var bundles = synchronizer.MarkRead(new BatchMarkReadRequest([new MarkReadRequest(mail, isRead)]));
        await synchronizer.ExecuteNativeRequestsAsync(bundles);
    }

    private static TestContext CreateContext((string Url, string Email, string Password) credentials)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Exchange Live Test",
            Address = credentials.Email,
            ProviderType = MailProviderType.Exchange,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                IncomingServer = credentials.Url,
                IncomingServerUsername = credentials.Email,
                IncomingServerPassword = credentials.Password,
            }
        };

        var folders = new List<MailItemFolder>();
        var storedMails = new Dictionary<string, MailCopy>();

        var changeProcessor = new Mock<IExchangeChangeProcessor>();

        changeProcessor.Setup(x => x.GetLocalFoldersAsync(account.Id))
            .ReturnsAsync(() => folders.ToList());

        changeProcessor.Setup(x => x.InsertFolderAsync(It.IsAny<MailItemFolder>()))
            .Returns((MailItemFolder folder) => { folders.Add(folder); return Task.CompletedTask; });

        changeProcessor.Setup(x => x.UpdateFolderAsync(It.IsAny<MailItemFolder>()))
            .Returns(Task.CompletedTask);

        changeProcessor.Setup(x => x.DeleteFolderAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((Guid _, string remoteFolderId) => { folders.RemoveAll(f => f.RemoteFolderId == remoteFolderId); return Task.CompletedTask; });

        changeProcessor.Setup(x => x.CreateMailsAsync(account.Id, It.IsAny<IReadOnlyList<NewMailItemPackage>>()))
            .Returns((Guid _, IReadOnlyList<NewMailItemPackage> packages) =>
            {
                foreach (var package in packages)
                    storedMails[package.Copy.Id] = package.Copy;
                return Task.CompletedTask;
            });

        changeProcessor.Setup(x => x.DeleteMailsAsync(account.Id, It.IsAny<IEnumerable<string>>()))
            .Returns((Guid _, IEnumerable<string> ids) =>
            {
                foreach (var id in ids)
                    storedMails.Remove(id);
                return Task.CompletedTask;
            });

        changeProcessor.Setup(x => x.ChangeMailReadStatusAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string mailCopyId, bool isRead) =>
            {
                if (storedMails.TryGetValue(mailCopyId, out var copy))
                    copy.IsRead = isRead;
                return Task.CompletedTask;
            });

        var synchronizer = new ExchangeSynchronizer(
            account,
            new ExchangeNtlmAuthenticator(),
            changeProcessor.Object,
            Mock.Of<IExchangeSynchronizerErrorHandlerFactory>());

        return new TestContext(account, folders, storedMails, synchronizer);
    }

    private sealed record TestContext(
        MailAccount Account,
        List<MailItemFolder> Folders,
        Dictionary<string, MailCopy> StoredMails,
        ExchangeSynchronizer Synchronizer);
}
