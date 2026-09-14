using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Errors.Outlook;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class MailDeltaSynchronizationTests
{
    [Fact]
    public async Task Gmail_read_change_is_not_replayed_on_the_next_unchanged_sync()
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        var requests = 0;
        var synchronizer = Gmail(account, processor, request =>
        {
            if (++requests == 1)
            {
                request.RequestUri!.Query.Should().Contain("startHistoryId=100");
                return Json("""{"historyId":"200","history":[{"labelsRemoved":[{"message":{"id":"m1"},"labelIds":["UNREAD"]}]}]}""");
            }

            request.RequestUri!.Query.Should().Contain("startHistoryId=200");
            return Json("""{"historyId":"200"}""");
        });

        await GmailDelta(synchronizer);
        await GmailDelta(synchronizer);

        requests.Should().Be(2);
        account.SynchronizationDeltaIdentifier.Should().Be("200");
        processor.Verify(x => x.ApplyMailStateUpdatesAsync(It.Is<IEnumerable<MailCopyStateUpdate>>(updates =>
            updates.Single().MailCopyId == "m1" && updates.Single().IsRead == true)), Times.Once);
        processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, "200"), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gmail_pagination_keeps_the_original_cursor_until_the_final_page(bool failSecondPage)
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        var requests = 0;
        var synchronizer = Gmail(account, processor, request =>
        {
            requests++;
            request.RequestUri!.Query.Should().Contain("startHistoryId=100");
            account.SynchronizationDeltaIdentifier.Should().Be("100");
            processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, It.IsAny<string>()), Times.Never);
            if (requests == 1)
                return Json("""{"historyId":"200","nextPageToken":"page-2"}""");

            request.RequestUri.Query.Should().Contain("pageToken=page-2");
            return failSecondPage
                ? Json("""{"error":{"code":500,"message":"failed"}}""", HttpStatusCode.InternalServerError)
                : Json("""{"historyId":"200"}""");
        });

        Func<Task> sync = () => GmailDelta(synchronizer);
        if (failSecondPage)
        {
            await sync.Should().ThrowAsync<Exception>();
            account.SynchronizationDeltaIdentifier.Should().Be("100");
            processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, It.IsAny<string>()), Times.Never);
        }
        else
        {
            await sync();
            account.SynchronizationDeltaIdentifier.Should().Be("200");
            processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, "200"), Times.Once);
        }

        requests.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gmail_batch_or_persistence_failure_does_not_advance_history(bool failDownload)
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        processor.Setup(x => x.CreateMailsAsync(account.Id, It.IsAny<IReadOnlyList<NewMailItemPackage>>()))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));
        var synchronizer = Gmail(account, processor, request => request.Method == HttpMethod.Post
            ? Batch(failDownload ? 500 : 200, failDownload
                ? """{"error":{"code":500,"message":"download failed"}}"""
                : """{"id":"m1","historyId":"900","labelIds":["INBOX"]}""")
            : Json("""{"historyId":"200","history":[{"messagesAdded":[{"message":{"id":"m1"}}]}]}"""));

        Func<Task> sync = () => GmailDelta(synchronizer);
        await sync.Should().ThrowAsync<Exception>();

        account.SynchronizationDeltaIdentifier.Should().Be("100");
        processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, It.IsAny<string>()), Times.Never);
        processor.Verify(x => x.CreateMailsAsync(account.Id, It.IsAny<IReadOnlyList<NewMailItemPackage>>()),
            failDownload ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task Gmail_downloads_unknown_mail_from_label_changes_and_ignores_message_history_cursor()
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        var exists = false;
        processor.Setup(x => x.AreMailsExistsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(() => exists ? new List<string> { "m1" } : new List<string>());
        processor.Setup(x => x.CreateMailsAsync(account.Id, It.IsAny<IReadOnlyList<NewMailItemPackage>>()))
            .Callback(() => exists = true).Returns(Task.CompletedTask);
        var batchRequests = 0;
        var synchronizer = Gmail(account, processor, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                batchRequests++;
                return Batch(200, """{"id":"m1","historyId":"900","labelIds":["INBOX"]}""");
            }

            return Json("""{"historyId":"200","history":[{"labelsAdded":[{"message":{"id":"m1"},"labelIds":["INBOX"]}]}]}""");
        });

        await GmailDelta(synchronizer);
        batchRequests.Should().Be(1);
        exists.Should().BeTrue();
        account.SynchronizationDeltaIdentifier.Should().Be("200");
        processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, "900"), Times.Never);
    }

    [Fact]
    public async Task Gmail_bootstrap_captures_history_before_enumerating_mail()
    {
        var account = Account(string.Empty);
        var processor = GmailProcessor(account);
        processor.Setup(x => x.GetLocalFoldersAsync(account.Id)).ReturnsAsync(new List<MailItemFolder>());
        var paths = new List<string>();
        var synchronizer = Gmail(account, processor, request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/profile"))
                return Json("""{"historyId":"100"}""");

            request.RequestUri.Query.Should().Contain("startHistoryId=100");
            account.SynchronizationDeltaIdentifier.Should().BeEmpty();
            return Json("""{"historyId":"200"}""");
        });

        await Invoke(synchronizer, "PerformInitialSyncWithHistoryAsync", new MailSynchronizationOptions(), CancellationToken.None);
        paths.Should().HaveCount(2);
        paths[0].Should().EndWith("/profile");
        paths[1].Should().EndWith("/history");
        account.SynchronizationDeltaIdentifier.Should().Be("200");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outlook_request_or_item_failure_does_not_commit_or_report_success(bool failItem)
    {
        var account = Account("folders-cursor");
        var folder = Folder();
        var processor = new Mock<IOutlookChangeProcessor>();
        processor.Setup(x => x.DeleteAssignmentAsync(account.Id, "m1", folder.RemoteFolderId))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));
        var synchronizer = Outlook(account, processor, _ => failItem
            ? Json("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/next","value":[{"id":"m1","@removed":{"reason":"deleted"}}]}""")
            : Json("""{"error":{"code":"ErrorAccessDenied","message":"denied"}}""", HttpStatusCode.Forbidden));

        Func<Task> sync = () => Invoke(synchronizer, "SynchronizeFolderAsync", folder, CancellationToken.None);
        await sync.Should().ThrowAsync<Exception>();

        processor.Verify(x => x.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, It.IsAny<string>()), Times.Never);
        processor.Verify(x => x.UpdateFolderLastSyncDateAsync(folder.Id), Times.Never);
        folder.DeltaToken.Should().Be("https://graph.microsoft.com/v1.0/start");
    }

    [Fact]
    public async Task Outlook_expired_folder_cursor_recovers_without_erasing_account_cache()
    {
        var account = Account("folders-cursor");
        var folder = Folder();
        var processor = new Mock<IOutlookChangeProcessor>();
        processor.Setup(x => x.GetMailsByFolderIdAsync(folder.Id)).ReturnsAsync(new List<MailCopy>());
        var factory = new Mock<IOutlookSynchronizerErrorHandlerFactory>();
        factory.Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .Returns<SynchronizerErrorContext>(context => new DeltaTokenExpiredHandler(processor.Object).HandleAsync(context));
        var requests = 0;
        var synchronizer = Outlook(account, processor, _ => ++requests == 1
            ? Json("""{"error":{"code":"SyncStateNotFound","message":"expired"}}""", HttpStatusCode.Gone)
            : Json("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/fresh","value":[]}"""), factory.Object);

        await Invoke(synchronizer, "SynchronizeFolderAsync", folder, CancellationToken.None);

        requests.Should().Be(2);
        folder.DeltaToken.Should().Be("https://graph.microsoft.com/v1.0/fresh");
        account.SynchronizationDeltaIdentifier.Should().Be("folders-cursor");
        processor.Verify(x => x.DeleteUserMailCacheAsync(It.IsAny<Guid>()), Times.Never);
        processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        processor.Verify(x => x.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, string.Empty), Times.Once);
    }

    [Fact]
    public async Task Outlook_recovery_removes_only_confirmed_missing_membership_and_preserves_local_drafts()
    {
        var account = Account("folders-cursor");
        var folder = Folder();
        folder.DeltaToken = string.Empty;
        var processor = new Mock<IOutlookChangeProcessor>();
        processor.Setup(x => x.GetMailsByFolderIdAsync(folder.Id)).ReturnsAsync(new List<MailCopy>
        {
            new() { Id = "moved" },
            new() { Id = "older" },
            new() { Id = "local", DraftId = Wino.Core.Domain.Constants.LocalDraftStartPrefix + "1" }
        });
        var synchronizer = Outlook(account, processor, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/moved")) return Json("""{"id":"moved","parentFolderId":"elsewhere"}""");
            if (path.EndsWith("/older")) return Json("""{"id":"older","parentFolderId":"inbox"}""");
            path.Should().EndWith("/delta()");
            return Json("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/fresh","value":[]}""");
        });

        await Invoke(synchronizer, "SynchronizeFolderAsync", folder, CancellationToken.None);

        processor.Verify(x => x.DeleteAssignmentAsync(account.Id, "moved", "inbox"), Times.Once);
        processor.Verify(x => x.DeleteAssignmentAsync(account.Id, "older", "inbox"), Times.Never);
        processor.Verify(x => x.DeleteAssignmentAsync(account.Id, "local", "inbox"), Times.Never);
    }

    [Fact]
    public async Task Gmail_replays_a_failed_round_from_the_persisted_cursor()
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        var applications = 0;
        processor.Setup(x => x.DeleteMailsAsync(account.Id, It.IsAny<IEnumerable<string>>()))
            .Returns(() => ++applications == 1 ? Task.FromException(new InvalidOperationException("Database unavailable")) : Task.CompletedTask);
        var synchronizer = Gmail(account, processor, request =>
        {
            request.RequestUri!.Query.Should().Contain("startHistoryId=100");
            return Json("""{"historyId":"200","history":[{"messagesDeleted":[{"message":{"id":"deleted"}}]}]}""");
        });

        Func<Task> sync = () => GmailDelta(synchronizer);
        await sync.Should().ThrowAsync<InvalidOperationException>();
        account.SynchronizationDeltaIdentifier.Should().Be("100");
        await sync();

        applications.Should().Be(2);
        account.SynchronizationDeltaIdentifier.Should().Be("200");
    }

    [Fact]
    public async Task Gmail_message_404_is_a_deletion_not_expired_mailbox_history()
    {
        var account = Account("100");
        var processor = GmailProcessor(account);
        var synchronizer = Gmail(account, processor, request => request.Method == HttpMethod.Post
            ? Batch(404, """{"error":{"code":404,"message":"deleted"}}""")
            : Json("""{"historyId":"200","history":[{"messagesAdded":[{"message":{"id":"gone"}}]}]}"""));

        await GmailDelta(synchronizer);

        account.SynchronizationDeltaIdentifier.Should().Be("200");
        processor.Verify(x => x.DeleteMailsAsync(account.Id, It.Is<IEnumerable<string>>(ids => ids.Single() == "gone")), Times.Once);
        processor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, null!), Times.Never);
    }

    [Fact]
    public async Task Outlook_cancellation_during_item_processing_preserves_the_cursor()
    {
        var account = Account("folders-cursor");
        var folder = Folder();
        var processor = new Mock<IOutlookChangeProcessor>();
        processor.Setup(x => x.DeleteAssignmentAsync(account.Id, "m1", folder.RemoteFolderId))
            .ThrowsAsync(new OperationCanceledException());
        var synchronizer = Outlook(account, processor, _ =>
            Json("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/next","value":[{"id":"m1","@removed":{"reason":"deleted"}}]}"""));

        Func<Task> sync = () => Invoke(synchronizer, "SynchronizeFolderAsync", folder, CancellationToken.None);
        await sync.Should().ThrowAsync<OperationCanceledException>();
        processor.Verify(x => x.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, It.IsAny<string>()), Times.Never);
        processor.Verify(x => x.UpdateFolderLastSyncDateAsync(folder.Id), Times.Never);
    }

    [Fact]
    public async Task Outlook_refreshes_existing_draft_content_without_reporting_new_mail()
    {
        var account = Account("folders-cursor");
        var folder = Folder();
        var processor = new Mock<IOutlookChangeProcessor>();
        processor.Setup(x => x.IsMailExistsInFolderAsync("draft", folder.Id)).ReturnsAsync(true);
        NewMailItemPackage? saved = null;
        processor.Setup(x => x.CreateMailAsync(account.Id, It.IsAny<NewMailItemPackage>()))
            .Callback<Guid, NewMailItemPackage>((_, package) => saved = package).ReturnsAsync(false);
        var synchronizer = Outlook(account, processor, request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/$value"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("From: test@example.com\r\nTo: recipient@example.com\r\nSubject: Edited\r\n\r\nUpdated body")
                };

            return Json("""{"id":"draft","isDraft":true,"subject":"Edited","receivedDateTime":"2026-09-01T00:00:00Z"}""");
        });
        var downloaded = new List<string>();

        await Invoke(synchronizer, "HandleItemRetrievedAsync", new Microsoft.Graph.Models.Message { Id = "draft", IsDraft = true },
            folder, downloaded, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.Copy.Subject.Should().Be("Edited");
        saved.Mime!.TextBody.Trim().Should().Be("Updated body");
        downloaded.Should().BeEmpty();
    }

    [Fact]
    public async Task Outlook_folder_list_expiration_preserves_message_cursors_and_cached_mail()
    {
        var account = Account("expired");
        var processor = new Mock<IOutlookChangeProcessor>(MockBehavior.Strict);
        processor.Setup(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, string.Empty)).ReturnsAsync(string.Empty);
        var handler = new DeltaTokenExpiredHandler(processor.Object);

        (await handler.HandleAsync(new SynchronizerErrorContext { Account = account, ErrorCode = 410 })).Should().BeTrue();

        account.SynchronizationDeltaIdentifier.Should().BeEmpty();
        processor.VerifyAll();
    }

    private static MailAccount Account(string cursor) => new()
    {
        Id = Guid.NewGuid(), Name = "Test", Address = "test@example.com", SynchronizationDeltaIdentifier = cursor
    };

    private static MailItemFolder Folder() => new()
    {
        Id = Guid.NewGuid(), RemoteFolderId = "inbox", FolderName = "Inbox", DeltaToken = "https://graph.microsoft.com/v1.0/start"
    };

    private static Mock<IGmailChangeProcessor> GmailProcessor(MailAccount account)
    {
        var processor = new Mock<IGmailChangeProcessor>();
        processor.Setup(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(account.Id, It.IsAny<string>()))
            .ReturnsAsync((Guid _, string value) => value);
        processor.Setup(x => x.AreMailsExistsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
        return processor;
    }

    private static GmailSynchronizer Gmail(MailAccount account, Mock<IGmailChangeProcessor> processor,
        Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(account, processor.Object, Mock.Of<IGmailSynchronizerErrorHandlerFactory>(), new Handler(response));

    private static OutlookSynchronizer Outlook(MailAccount account, Mock<IOutlookChangeProcessor> processor,
        Func<HttpRequestMessage, HttpResponseMessage> response, IOutlookSynchronizerErrorHandlerFactory? factory = null)
    {
        var synchronizer = new OutlookSynchronizer(account, Mock.Of<IAuthenticator>(), processor.Object,
            factory ?? Mock.Of<IOutlookSynchronizerErrorHandlerFactory>(), Mock.Of<IMailCategoryService>());
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: new HttpClient(new Handler(response)));
        typeof(OutlookSynchronizer).GetField("_graphClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(synchronizer, new GraphServiceClient(adapter));
        return synchronizer;
    }

    private static Task GmailDelta(GmailSynchronizer synchronizer) =>
        Invoke(synchronizer, "SynchronizeDeltaAsync", new MailSynchronizationOptions(), CancellationToken.None, null!);

    private static async Task Invoke(object target, string method, params object[] arguments)
    {
        var task = (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
        await task;
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Batch(int status, string json)
    {
        var content = $"--test\r\nContent-Type: application/http\r\nContent-ID: <response-item-0>\r\n\r\nHTTP/1.1 {status} Status\r\nContent-Type: application/json\r\n\r\n{json}\r\n--test--\r\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/mixed; boundary=test");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
