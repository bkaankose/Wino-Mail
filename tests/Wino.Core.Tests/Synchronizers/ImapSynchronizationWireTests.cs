using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Services.Extensions;
using Xunit;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ImapSynchronizationWireTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("condstore")]
    [InlineData("qresync")]
    [InlineData("qresync-rejected")]
    [InlineData("condstore-rejected")]
    [InlineData("nomodseq")]
    public async Task RealMailKit_ReconcilesFlagsAndVanishedMessages_WithOneSelectNormally(string mode)
    {
        await using var server = new ReplayServer(mode);
        using var client = new WinoImapClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None, timeout.Token);
        if (mode.StartsWith("qresync"))
        {
            await client.EnableQuickResyncAsync(timeout.Token);
            client.IsQResyncEnabled = true;
        }
        var folder = new MailItemFolder
        {
            Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), RemoteFolderId = "INBOX", FolderName = "Inbox",
            UidValidity = 7, HighestModeSeq = 20, HighestKnownUid = 3, LastUidReconcileUtc = DateTime.UtcNow
        };
        var known = Enumerable.Range(1, 3).Select(uid => new MailCopy
        {
            Id = MailkitClientExtensions.CreateUid(folder.Id, (uint)uid), FolderId = folder.Id,
            ImapUid = (uint)uid, ImapUidValidity = 7
        }).ToList();
        var mail = new Mock<IMailService>();
        mail.Setup(x => x.GetImapSynchronizationMailsAsync(folder.Id)).ReturnsAsync(known);
        mail.Setup(x => x.GetExistingMailsAsync(folder.Id, It.IsAny<IEnumerable<UniqueId>>()))
            .Returns<Guid, IEnumerable<UniqueId>>((_, uids) => Task.FromResult(known.Where(m => uids.Contains(new UniqueId(m.ImapUid))).ToList()));
        var updates = new List<MailCopyStateUpdate>();
        mail.Setup(x => x.ApplyMailStateUpdatesAsync(It.IsAny<IEnumerable<MailCopyStateUpdate>>()))
            .Callback<IEnumerable<MailCopyStateUpdate>>(items => updates.AddRange(items)).Returns(Task.CompletedTask);
        var deleted = new List<string>();
        mail.Setup(x => x.DeleteMailsAsync(folder.MailAccountId, It.IsAny<IEnumerable<string>>()))
            .Callback<Guid, IEnumerable<string>>((_, ids) => deleted.AddRange(ids)).Returns(Task.CompletedTask);
        var folders = new Mock<IFolderService>();
        folders.Setup(x => x.GetKnownUidsForFolderAsync(folder.Id)).ReturnsAsync([1u, 2u, 3u]);
        var sut = new UnifiedImapSynchronizer(folders.Object, mail.Object, Mock.Of<IImapSynchronizerErrorHandlerFactory>());
        var result = await sut.SynchronizeFolderAsync(client, folder, Mock.Of<IImapSynchronizer>(), "127.0.0.1", timeout.Token);

        result.Success.Should().BeTrue();
        updates.Should().ContainSingle(x => x.MailCopyId == known[2].Id && x.IsRead == true && x.IsFlagged == true);
        deleted.Distinct().Should().Equal(known[1].Id);
        var commands = server.Commands.ToArray();
        commands.Count(command => command.StartsWith("EXAMINE ")).Should().Be(mode == "qresync-rejected" ? 2 : 1);
        commands.Should().NotContain(command => command.Contains("UNSEEN") || command.Contains("SEARCH FLAGGED"));
        commands.Should().NotContain(command => command.StartsWith("CLOSE") || command.Contains("EXPUNGE"));
        if (mode == "qresync")
        {
            commands.Should().Contain(command => command.Contains("QRESYNC (7 20 1:3)"));
            commands.Should().NotContain(command => command.StartsWith("UID FETCH"));
        }
        else
        {
            commands.Should().Contain(command => command.StartsWith("UID FETCH 1:3 (UID FLAGS)"));
            commands.Count(command => command.StartsWith("UID FETCH")).Should().Be(mode == "condstore-rejected" ? 2 : 1);
        }
        if (mode is "legacy" or "nomodseq" or "condstore-rejected") folder.HighestModeSeq.Should().Be(0);
        else folder.HighestModeSeq.Should().Be(30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyFetchFailure_PreservesLocalMessage_AndChecksUidValidity(bool changedValidity)
    {
        await using var server = new ReplayServer("body-no");
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(Path.GetTempPath());
        var account = new MailAccount
        {
            Id = Guid.NewGuid(), ProviderType = MailProviderType.IMAP4,
            ServerInformation = new CustomServerInformation
            {
                IncomingServer = "127.0.0.1", IncomingServerPort = server.Port.ToString(),
                IncomingServerSocketOption = ImapConnectionSecurity.None,
                IncomingAuthenticationMethod = ImapAuthenticationMethod.None, MaxConcurrentClients = 1
            }
        };
        var processor = new Mock<IImapChangeProcessor>();
        var errors = Mock.Of<IImapSynchronizerErrorHandlerFactory>();
        var sut = new ImapSynchronizer(account, processor.Object, configuration.Object,
            new UnifiedImapSynchronizer(Mock.Of<IFolderService>(), Mock.Of<IMailService>(), errors), errors,
            Mock.Of<ICalDavClient>(), Mock.Of<IAutoDiscoveryService>(), Mock.Of<ICalendarService>());
        try
        {
            var copy = new MailCopy
            {
                Id = "cached-message", ImapUid = 3, ImapUidValidity = changedValidity ? 8u : 7u,
                AssignedFolder = new MailItemFolder { RemoteFolderId = "INBOX", UidValidity = 7 }
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var download = () => sut.DownloadMissingMimeMessageAsync(copy, cancellationToken: timeout.Token);
            if (changedValidity) await download.Should().ThrowAsync<InvalidOperationException>();
            else await download.Should().ThrowAsync<ImapCommandException>();
            processor.Verify(x => x.DeleteMailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            server.Commands.Count(command => command.StartsWith("UID FETCH")).Should().Be(changedValidity ? 0 : 1);
        }
        finally { await sut.KillSynchronizerAsync(); }
    }

    private sealed class ReplayServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private readonly string mode;
        public ConcurrentQueue<string> Commands { get; } = new();
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public ReplayServer(string mode)
        {
            this.mode = mode;
            listener.Start();
            serving = Serve();
        }
        private async Task Serve()
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                var capabilities = "IMAP4rev1 UNSELECT" + (mode != "legacy" ? " CONDSTORE" : "")
                    + (mode.StartsWith("qresync") ? " QRESYNC ENABLE" : "");
                await writer.WriteLineAsync("* PREAUTH [CAPABILITY " + capabilities + "] replay");
                while (!stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stop.Token);
                    if (line == null) break;
                    var split = line.IndexOf(' ');
                    var tag = line[..split];
                    var command = line[(split + 1)..];
                    Commands.Enqueue(command);
                    if (command == "CAPABILITY") await writer.WriteLineAsync("* CAPABILITY " + capabilities);
                    else if (command.StartsWith("ENABLE")) await writer.WriteLineAsync("* ENABLED QRESYNC CONDSTORE");
                    else if (command.StartsWith("LIST")) await writer.WriteLineAsync("* LIST () \"/\" \"INBOX\"");
                    else if (command.StartsWith("EXAMINE"))
                    {
                        if (mode == "qresync-rejected" && command.Contains("QRESYNC"))
                        {
                            await writer.WriteLineAsync(tag + " BAD QRESYNC unsupported for this mailbox");
                            continue;
                        }
                        await writer.WriteLineAsync("* FLAGS (\\Seen \\Flagged \\Deleted)");
                        await writer.WriteLineAsync("* 2 EXISTS");
                        await writer.WriteLineAsync("* OK [UIDVALIDITY 7]");
                        await writer.WriteLineAsync("* OK [UIDNEXT 4]");
                        if (mode == "nomodseq") await writer.WriteLineAsync("* OK [NOMODSEQ]");
                        else if (mode != "legacy") await writer.WriteLineAsync("* OK [HIGHESTMODSEQ 30]");
                        if (mode == "qresync")
                        {
                            await writer.WriteLineAsync("* VANISHED (EARLIER) 2");
                            await writer.WriteLineAsync("* 2 FETCH (UID 3 FLAGS (\\Seen \\Flagged) MODSEQ (30))");
                        }
                    }
                    else if (command.StartsWith("UID SEARCH")) await writer.WriteLineAsync("* SEARCH 1 3");
                    else if (command.StartsWith("UID FETCH"))
                    {
                        if (mode == "body-no")
                        {
                            await writer.WriteLineAsync(tag + " NO temporary message access failure");
                            continue;
                        }
                        if (mode == "condstore-rejected" && command.Contains("CHANGEDSINCE"))
                        {
                            await writer.WriteLineAsync(tag + " BAD CHANGEDSINCE unsupported");
                            continue;
                        }
                        if (!command.Contains("CHANGEDSINCE")) await writer.WriteLineAsync("* 1 FETCH (UID 1 FLAGS ())");
                        await writer.WriteLineAsync("* 2 FETCH (UID 3 FLAGS (\\Seen \\Flagged))");
                    }
                    else if (command is not "UNSELECT" and not "NOOP") throw new InvalidOperationException("Unexpected IMAP command: " + command);
                    await writer.WriteLineAsync(tag + (command.StartsWith("EXAMINE") ? " OK [READ-ONLY] completed" : " OK completed"));
                }
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}
