using System.Net;
using System.Text;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mail;
using Wino.Mail.AI.Abstractions;
using Wino.Services;
using MimeKit;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class SemanticSynchronizerBodyTests
{
    [Fact]
    public async Task ExistingLocalMime_IsUsedWithoutResolvingOrCallingProvider()
    {
        var accountId = Guid.NewGuid();
        var uncachedCopyFileId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var mime = new MimeMessage { Body = new TextPart("plain") { Text = "Already downloaded" } };
        var mimeFiles = new Mock<IMimeFileService>(MockBehavior.Strict);
        mimeFiles.Setup(x => x.IsMimeExistAsync(accountId, uncachedCopyFileId)).ReturnsAsync(false);
        mimeFiles.Setup(x => x.IsMimeExistAsync(accountId, fileId)).ReturnsAsync(true);
        mimeFiles.Setup(x => x.GetMimeMessageInformationAsync(fileId, accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MimeMessageInformation(mime, "local"));
        var providerWasCalled = false;

        var content = await IntelligenceMessageContextResolver.ResolveContentAsync(
            mimeFiles.Object,
            accountId,
            [uncachedCopyFileId, fileId],
            () =>
            {
                providerWasCalled = true;
                return Task.FromResult(new SemanticMailContent(
                    new MailBodyContent(MailBodyFormat.PlainText, "remote"),
                    [],
                    []));
            });

        Assert.False(providerWasCalled);
        Assert.Equal("Already downloaded", content.Body.Content);
        mimeFiles.VerifyAll();
    }

    [Fact]
    public async Task Gmail_UsesSynchronizerClientAndRequestsOnlyParsedBodyFields()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Visible Gmail body"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handler = new RecordingHandler(
            "{\"payload\":{\"headers\":[{\"name\":\"To\",\"value\":\"Wino User <mail@example.com>\"}],\"mimeType\":\"multipart/alternative\",\"filename\":\"\",\"body\":{},\"parts\":[{\"mimeType\":\"text/plain\",\"filename\":\"\",\"body\":{\"data\":\"" + encoded + "\"}}]}}");
        var synchronizer = new GmailSynchronizer(
            Account(),
            Mock.Of<IGmailChangeProcessor>(),
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>(),
            handler);

        var body = await synchronizer.GetSemanticBodyAsync(
            new MailBodyLocator("gmail:v1:key", "INBOX", ProviderMessageId: "provider-id"));

        Assert.Equal(MailBodyFormat.PlainText, body.Body.Format);
        Assert.Equal("Visible Gmail body", body.Body.Content);
        Assert.Equal("mail@example.com", Assert.Single(body.ToRecipients));
        var request = Assert.Single(handler.Requests);
        var query = Uri.UnescapeDataString(request.Query);
        Assert.Contains("format=full", query, StringComparison.Ordinal);
        Assert.Contains("fields=payload(", query, StringComparison.Ordinal);
        Assert.DoesNotContain("format=raw", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snippet", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("headers", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/raw", request.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gmail_ExternalTextPart_DownloadsOnlyThatBodyPart()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("External text body"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handler = new RecordingHandler(
            "{\"payload\":{\"mimeType\":\"text/plain\",\"filename\":\"\",\"body\":{\"attachmentId\":\"text-body\"}}}",
            "{\"data\":\"" + encoded + "\"}");
        var synchronizer = new GmailSynchronizer(
            Account(),
            Mock.Of<IGmailChangeProcessor>(),
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>(),
            handler);

        var body = await synchronizer.GetSemanticBodyAsync(
            new MailBodyLocator("gmail:v1:key", "INBOX", ProviderMessageId: "provider-id"));

        Assert.Equal("External text body", body.Body.Content);
        Assert.Equal(2, handler.Requests.Count);
        var bodyRequest = handler.Requests[1];
        Assert.EndsWith("/messages/provider-id/attachments/text-body", bodyRequest.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("fields=data", Uri.UnescapeDataString(bodyRequest.Query), StringComparison.Ordinal);
    }

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Address = "mail@example.com",
        AuthenticationAddress = "mail@example.com",
        ProviderType = MailProviderType.Gmail
    };

    private sealed class RecordingHandler(params string[] responseJson) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responseJson);
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }
}
