using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class DraftProviderUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static MailCopy Draft() => new()
    {
        UniqueId = Guid.NewGuid(), Id = "old", DraftId = "draft", IsDraft = true,
        ImapUid = 10, ImapUidValidity = 5, FolderId = Guid.NewGuid(),
        AssignedAccount = new() { Id = Guid.NewGuid(), ServerInformation = new() { IncomingServer = "imap.example.test" } },
        AssignedFolder = new() { RemoteFolderId = "Drafts" }
    };
    private static DraftUpdateSnapshot Snapshot(MailCopy draft, bool attachment = false)
    {
        using var mime = new MimeMessage { Subject = "", MessageId = "message@example.test" };
        mime.From.Add(new MailboxAddress("Me", "me@example.test"));
        var body = new BodyBuilder { TextBody = "latest body" };
        if (attachment) body.Attachments.Add("new.txt", Encoding.UTF8.GetBytes("data"));
        mime.Body = body.ToMessageBody();
        using var bytes = new MemoryStream(); mime.WriteTo(bytes);
        return new(draft.AssignedAccount.Id, draft.UniqueId, bytes.ToArray());
    }

    [Fact]
    public async Task Gmail_updates_complete_mime_and_returns_replacement_identity_without_state_changes()
    {
        var draft = Draft();
        using var handler = new Handler(async (request, token) =>
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.AbsolutePath.Should().EndWith("/drafts/draft");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var raw = json.RootElement.GetProperty("message").GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
            using var mime = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw)));
            mime.TextBody.Should().Contain("latest body"); mime.Attachments.Should().ContainSingle();
            return Json("{\"id\":\"draft\",\"message\":{\"id\":\"new-id\",\"threadId\":\"thread\"}}");
        });
        var synchronizer = new GmailSynchronizer(draft.AssignedAccount, Mock.Of<IGmailChangeProcessor>(),
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>(), handler);
        var previousState = synchronizer.State;
        var result = await synchronizer.UpdateDraftAsync(Snapshot(draft, true), draft);
        result.MessageId.Should().Be("new-id"); result.DraftId.Should().Be("draft");
        synchronizer.State.Should().Be(previousState);
    }

    [Fact]
    public async Task Gmail_canceled_response_reconciles_identity_before_returning()
    {
        var draft = Draft(); using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token);
            }
            request.Method.Should().Be(HttpMethod.Get);
            return Task.FromResult(Json("{\"id\":\"draft\",\"message\":{\"id\":\"accepted-id\"}}"));
        });
        var processor = new Mock<IGmailChangeProcessor>();
        var synchronizer = new GmailSynchronizer(draft.AssignedAccount, processor.Object,
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>(), handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.UpdateDraftAsync(Snapshot(draft), draft, cancellation.Token));
        processor.Verify(x => x.UpdateDraftIdentityAsync(draft.AssignedAccount.Id, draft.UniqueId,
            It.Is<DraftUpdateIdentity>(i => i.MessageId == "accepted-id")), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outlook_patches_details_before_reconciling_attachments(bool alreadyUploaded)
    {
        var draft = Draft(); var methods = new List<string>();
        using var handler = new Handler(async (request, token) =>
        {
            methods.Add(request.Method.Method);
            if (request.Method == HttpMethod.Patch)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var body = json.RootElement;
                body.TryGetProperty("isDraft", out _).Should().BeFalse();
                body.TryGetProperty("conversationId", out _).Should().BeFalse();
                body.GetProperty("subject").GetString().Should().BeEmpty();
                body.GetProperty("toRecipients").GetArrayLength().Should().Be(0);
                body.GetProperty("ccRecipients").GetArrayLength().Should().Be(0);
                body.GetProperty("bccRecipients").GetArrayLength().Should().Be(0);
                body.GetProperty("replyTo").GetArrayLength().Should().Be(0);
                return Json("{\"id\":\"old\"}");
            }
            if (request.Method == HttpMethod.Get)
                return Json(alreadyUploaded
                    ? "{\"value\":[{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"keep\",\"name\":\"new.txt\",\"contentType\":\"text/plain\",\"contentBytes\":\"ZGF0YQ==\"}]}"
                    : "{\"value\":[{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"id\":\"remove\",\"name\":\"old.txt\",\"contentType\":\"text/plain\",\"contentBytes\":\"b2xk\"}]}");
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            request.Method.Should().Be(HttpMethod.Post);
            return Json("{\"id\":\"added\",\"@odata.type\":\"#microsoft.graph.fileAttachment\"}");
        });
        var synchronizer = new OutlookSynchronizer(draft.AssignedAccount, Mock.Of<IAuthenticator>(), Mock.Of<IOutlookChangeProcessor>(),
            Mock.Of<IOutlookSynchronizerErrorHandlerFactory>(), Mock.Of<IMailCategoryService>());
        using var http = new HttpClient(handler);
        typeof(OutlookSynchronizer).GetField("_graphClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(synchronizer, new GraphServiceClient(new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)));
        await synchronizer.UpdateDraftAsync(Snapshot(draft, true), draft);
        methods.Should().Equal(alreadyUploaded ? ["PATCH", "GET"] : new[] { "PATCH", "GET", "DELETE", "POST" });
    }

    [Fact]
    public void Outlook_attachment_comparison_includes_content_and_inline_identity()
    {
        var a = new Microsoft.Graph.Models.FileAttachment { Name = "image.png", ContentBytes = [1], ContentId = "cid", IsInline = true };
        var b = new Microsoft.Graph.Models.FileAttachment { Name = "image.png", ContentBytes = [1], ContentId = "cid", IsInline = true };
        OutlookSynchronizer.SameDraftAttachment(a, b).Should().BeTrue();
        b.ContentId = "other"; OutlookSynchronizer.SameDraftAttachment(a, b).Should().BeFalse();
        b.ContentId = "cid"; b.ContentBytes = [2]; OutlookSynchronizer.SameDraftAttachment(a, b).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imap_persists_new_identity_before_deleting_only_old_uid(bool uidPlus)
    {
        var draft = Draft(); var order = new List<string>();
        var folder = new Mock<IMailFolder>(); var client = new Mock<IImapClient>(); var processor = new Mock<IImapChangeProcessor>();
        client.Setup(x => x.GetFolderAsync("Drafts", It.IsAny<CancellationToken>())).ReturnsAsync(folder.Object);
        client.SetupGet(x => x.Capabilities).Returns(uidPlus ? ImapCapabilities.UidPlus : ImapCapabilities.None);
        folder.SetupGet(x => x.UidValidity).Returns(5);
        folder.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<UniqueId> { new(10) });
        folder.Setup(x => x.AppendAsync(It.IsAny<IAppendRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("append")).ReturnsAsync(new UniqueId(20));
        processor.Setup(x => x.UpdateDraftIdentityAsync(draft.AssignedAccount.Id, draft.UniqueId, It.IsAny<DraftUpdateIdentity>()))
            .Callback(() => order.Add("identity")).Returns(Task.CompletedTask);
        folder.Setup(x => x.StoreAsync(It.Is<IList<UniqueId>>(ids => ids.Count == 1 && ids[0].Id == 10), It.IsAny<IStoreFlagsRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("delete")).ReturnsAsync([]);
        var config = Mock.Of<IApplicationConfiguration>(x => x.ApplicationDataFolderPath == Path.GetTempPath());
        var synchronizer = new ImapSynchronizer(draft.AssignedAccount, processor.Object, config, null!,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(), Mock.Of<ICalDavClient>(), Mock.Of<IAutoDiscoveryService>(), Mock.Of<ICalendarService>());
        var identity = await synchronizer.UpdateDraftOnClientAsync(client.Object, Snapshot(draft), draft, CancellationToken.None);
        identity.ImapUid.Should().Be(20);
        order.Should().Equal("append", "identity", "delete");
        folder.Verify(x => x.ExpungeAsync(It.Is<IList<UniqueId>>(ids => ids.Count == 1 && ids[0].Id == 10), It.IsAny<CancellationToken>()), uidPlus ? Times.Once() : Times.Never());
        folder.Verify(x => x.ExpungeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData("validity")]
    [InlineData("missing")]
    [InlineData("append-fails")]
    [InlineData("uid-missing")]
    public async Task Imap_failure_never_deletes_unconfirmed_replacement(string failure)
    {
        var draft = Draft();
        var folder = new Mock<IMailFolder>(); var client = new Mock<IImapClient>();
        client.Setup(x => x.GetFolderAsync("Drafts", It.IsAny<CancellationToken>())).ReturnsAsync(folder.Object);
        folder.SetupGet(x => x.UidValidity).Returns(failure == "validity" ? 99u : 5u);
        var searches = 0;
        folder.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => failure == "missing" || Interlocked.Increment(ref searches) > 1 ? [] : new List<UniqueId> { new(10) });
        folder.Setup(x => x.AppendAsync(It.IsAny<IAppendRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => failure == "append-fails" ? Task.FromException<UniqueId?>(new IOException()) : Task.FromResult<UniqueId?>(null));
        var config = Mock.Of<IApplicationConfiguration>(x => x.ApplicationDataFolderPath == Path.GetTempPath());
        var processor = new Mock<IImapChangeProcessor>();
        var synchronizer = new ImapSynchronizer(draft.AssignedAccount, processor.Object, config, null!,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(), Mock.Of<ICalDavClient>(), Mock.Of<IAutoDiscoveryService>(), Mock.Of<ICalendarService>());
        await Assert.ThrowsAnyAsync<Exception>(() => synchronizer.UpdateDraftOnClientAsync(client.Object, Snapshot(draft), draft, CancellationToken.None));
        processor.Verify(x => x.UpdateDraftIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DraftUpdateIdentity>()), Times.Never);
        folder.Verify(x => x.StoreAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IStoreFlagsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        folder.Verify(x => x.ExpungeAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<CancellationToken>()), Times.Never);
        if (failure is "validity" or "missing")
            folder.Verify(x => x.AppendAsync(It.IsAny<IAppendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
