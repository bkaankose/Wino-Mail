using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ImapSynchronizerLiveTests
{
    private const string ManualSkipMessage = "Manual live IMAP test. Fill Server/Port/Username/Password placeholders and remove Skip to run.";

    // Replace placeholders with your own credentials when running these live tests.
    private const string Server = "imap.example.com";
    private const int Port = 993;
    private const string Username = "REPLACE_WITH_USERNAME";
    private const string Password = "REPLACE_WITH_PASSWORD";

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task InitialSynchronization_DownloadsInboxMetadata()
    {
        using var context = await CreateContextAsync();

        var result = await context.Synchronizer.SynchronizeMailsAsync(CreateCustomFolderSyncOptions(context.Account.Id, context.InboxFolder.Id));

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        result.FolderResults.Should().ContainSingle();
        result.FolderResults[0].Success.Should().BeTrue();
        result.FolderResults[0].DownloadedCount.Should().BeGreaterThan(0);
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task DeltaSynchronization_SecondRunDownloadsNoAdditionalMessages()
    {
        using var context = await CreateContextAsync();

        var initialResult = await context.Synchronizer.SynchronizeMailsAsync(CreateCustomFolderSyncOptions(context.Account.Id, context.InboxFolder.Id));
        initialResult.CompletedState.Should().Be(SynchronizationCompletedState.Success);

        var deltaResult = await context.Synchronizer.SynchronizeMailsAsync(CreateCustomFolderSyncOptions(context.Account.Id, context.InboxFolder.Id));

        deltaResult.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        deltaResult.FolderResults.Should().ContainSingle();
        deltaResult.FolderResults[0].DownloadedCount.Should().Be(0);
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task MarkFirstInboxMailUnreadThenRead_ValidatesReadFlagChanges()
    {
        using var context = await CreateContextAsync();

        var firstUid = await GetFirstInboxMessageUidAsync(context.Account);
        var mailCopy = CreateInboxMailCopy(context.InboxFolder, firstUid);

        await ExecuteMarkReadAsync(context.Synchronizer, mailCopy, isRead: false);
        (await GetIsSeenAsync(context.Account, firstUid)).Should().BeFalse();

        await ExecuteMarkReadAsync(context.Synchronizer, mailCopy, isRead: true);
        (await GetIsSeenAsync(context.Account, firstUid)).Should().BeTrue();
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task MarkFirstInboxMailReadThenUnread_ValidatesUnreadFlagChanges()
    {
        using var context = await CreateContextAsync();

        var firstUid = await GetFirstInboxMessageUidAsync(context.Account);
        var mailCopy = CreateInboxMailCopy(context.InboxFolder, firstUid);

        await ExecuteMarkReadAsync(context.Synchronizer, mailCopy, isRead: true);
        (await GetIsSeenAsync(context.Account, firstUid)).Should().BeTrue();

        await ExecuteMarkReadAsync(context.Synchronizer, mailCopy, isRead: false);
        (await GetIsSeenAsync(context.Account, firstUid)).Should().BeFalse();
    }

    private static MailSynchronizationOptions CreateCustomFolderSyncOptions(Guid accountId, Guid folderId)
        => new()
        {
            AccountId = accountId,
            Type = MailSynchronizationType.CustomFolders,
            SynchronizationFolderIds = [folderId]
        };

    private static async Task ExecuteMarkReadAsync(ImapSynchronizer synchronizer, MailCopy mailCopy, bool isRead)
    {
        var requests = synchronizer.MarkRead(new BatchMarkReadRequest([new MarkReadRequest(mailCopy, isRead)]));
        await synchronizer.ExecuteNativeRequestsAsync(requests);
    }

    private static MailCopy CreateInboxMailCopy(MailItemFolder folder, UniqueId uid)
        => new()
        {
            Id = MailkitClientExtensions.CreateUid(folder.Id, uid.Id),
            AssignedFolder = folder,
            IsRead = false,
            Subject = "Live test placeholder"
        };

    private static async Task<UniqueId> GetFirstInboxMessageUidAsync(MailAccount account)
    {
        using var client = await CreateConnectedClientAsync(account);
        var inbox = client.Inbox;

        await inbox.OpenAsync(FolderAccess.ReadWrite);

        var allUids = await inbox.SearchAsync(SearchQuery.All);
        allUids.Should().NotBeEmpty("Inbox must contain at least one message for mark-read live tests.");

        var firstUid = allUids.First();

        await inbox.CloseAsync();
        await client.DisconnectAsync(true);

        return firstUid;
    }

    private static async Task<bool> GetIsSeenAsync(MailAccount account, UniqueId uid)
    {
        using var client = await CreateConnectedClientAsync(account);
        var inbox = client.Inbox;

        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var summary = await inbox.FetchAsync([uid], MessageSummaryItems.Flags);

        await inbox.CloseAsync();
        await client.DisconnectAsync(true);

        summary.Should().ContainSingle();

        var flags = summary[0].Flags;
        flags.Should().NotBeNull();

        return flags!.Value.HasFlag(MessageFlags.Seen);
    }

    private static async Task<ImapClient> CreateConnectedClientAsync(MailAccount account)
    {
        var client = new ImapClient();

        await client.ConnectAsync(account.ServerInformation.IncomingServer, int.Parse(account.ServerInformation.IncomingServerPort), MailKit.Security.SecureSocketOptions.Auto);
        await client.AuthenticateAsync(account.ServerInformation.IncomingServerUsername, account.ServerInformation.IncomingServerPassword);

        return client;
    }

    private static async Task<LiveTestContext> CreateContextAsync()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "wino-imap-live-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Live Test",
            Address = Username,
            ProviderType = MailProviderType.IMAP4,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                IncomingServer = Server,
                IncomingServerPort = Port.ToString(),
                IncomingServerUsername = Username,
                IncomingServerPassword = Password,
                IncomingServerSocketOption = ImapConnectionSecurity.Auto,
                MaxConcurrentClients = 2
            }
        };

        var inboxFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            FolderName = "Inbox",
            RemoteFolderId = "INBOX",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSynchronizationEnabled = true,
            ShowUnreadCount = true,
            UidValidity = 0,
            HighestModeSeq = 0,
            HighestKnownUid = 0
        };

        var storedMails = new Dictionary<string, MailCopy>();

        var folderService = new Mock<IFolderService>();
        folderService.Setup(x => x.GetKnownUidsForFolderAsync(inboxFolder.Id))
            .ReturnsAsync(() => storedMails.Values.Select(m => MailkitClientExtensions.ResolveUid(m.Id)).ToList());
        folderService.Setup(x => x.UpdateFolderAsync(It.IsAny<MailItemFolder>())).Returns(Task.CompletedTask);
        folderService.Setup(x => x.UpdateFolderHighestModeSeqAsync(It.IsAny<Guid>(), It.IsAny<long>())).Returns(Task.CompletedTask);
        folderService.Setup(x => x.DeleteFolderAsync(It.IsAny<Guid>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var mailService = new Mock<IMailService>();
        mailService.Setup(x => x.GetMailsByFolderIdAsync(inboxFolder.Id)).ReturnsAsync(() => storedMails.Values.ToList());
        mailService.Setup(x => x.GetExistingMailsAsync(inboxFolder.Id, It.IsAny<IEnumerable<UniqueId>>()))
            .ReturnsAsync((Guid _, IEnumerable<UniqueId> ids) =>
                ids.Select(uid => MailkitClientExtensions.CreateUid(inboxFolder.Id, uid.Id))
                   .Where(storedMails.ContainsKey)
                   .Select(id => storedMails[id])
                   .ToList());
        mailService.Setup(x => x.CreateMailAsync(account.Id, It.IsAny<NewMailItemPackage>()))
            .ReturnsAsync((Guid _, NewMailItemPackage package) =>
            {
                storedMails[package.Copy.Id] = package.Copy;
                return true;
            });
        mailService.Setup(x => x.ChangeReadStatusAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string mailCopyId, bool isRead) =>
            {
                if (storedMails.TryGetValue(mailCopyId, out var copy))
                {
                    copy.IsRead = isRead;
                }

                return Task.CompletedTask;
            });
        mailService.Setup(x => x.ChangeFlagStatusAsync(It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        mailService.Setup(x => x.DeleteMailAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((Guid _, string mailCopyId) =>
            {
                storedMails.Remove(mailCopyId);
                return Task.CompletedTask;
            });

        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor.Setup(x => x.GetSynchronizationFoldersAsync(It.IsAny<MailSynchronizationOptions>()))
            .ReturnsAsync([inboxFolder]);
        changeProcessor.Setup(x => x.GetRecentMailIdsForFolderAsync(inboxFolder.Id, It.IsAny<int>()))
            .ReturnsAsync((Guid _, int count) => storedMails.Keys.Take(count).ToList());
        changeProcessor.Setup(x => x.GetDownloadedUnreadMailsAsync(account.Id, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([] as List<MailCopy>);

        var appConfiguration = new Mock<IApplicationConfiguration>();
        appConfiguration.SetupProperty(x => x.ApplicationDataFolderPath, tempDirectory);
        appConfiguration.SetupProperty(x => x.PublisherSharedFolderPath, tempDirectory);
        appConfiguration.SetupProperty(x => x.ApplicationTempFolderPath, tempDirectory);
        appConfiguration.SetupGet(x => x.SentryDNS).Returns(string.Empty);

        var unifiedSynchronizer = new UnifiedImapSynchronizer(
            folderService.Object,
            mailService.Object,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        var synchronizer = new ImapSynchronizer(
            account,
            changeProcessor.Object,
            appConfiguration.Object,
            unifiedSynchronizer,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            Mock.Of<ICalDavClient>(),
            Mock.Of<IAutoDiscoveryService>(),
            Mock.Of<ICalendarService>());

        return await Task.FromResult(new LiveTestContext(account, inboxFolder, synchronizer, tempDirectory));
    }

    private sealed class LiveTestContext(MailAccount account, MailItemFolder inboxFolder, ImapSynchronizer synchronizer, string tempDirectory) : IDisposable
    {
        public MailAccount Account { get; } = account;
        public MailItemFolder InboxFolder { get; } = inboxFolder;
        public ImapSynchronizer Synchronizer { get; } = synchronizer;

        public void Dispose()
        {
            Synchronizer.KillSynchronizerAsync().GetAwaiter().GetResult();

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
