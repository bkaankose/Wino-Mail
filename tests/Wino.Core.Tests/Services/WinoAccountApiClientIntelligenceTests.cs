using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Translations;
using Wino.Core.Tests.Helpers;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountApiClientIntelligenceTests
{
    [Fact]
    public async Task IntelligenceFeatureCalls_UseTypedRoutesApplicationLanguageAndEncryptedReplyContent()
    {
        var userId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = userId,
            Email = "intelligence@example.test",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });

        using var rsa = RSA.Create(2048);
        const string keyId = "test-intelligence-key";
        var encryptor = new PemContentEnvelopeEncryptor(new ContentEncryptionPublicKey(
            keyId,
            rsa.ExportSubjectPublicKeyInfoPem()));
        var decryptor = new PemContentEnvelopeDecryptor(new Dictionary<string, string>
        {
            [keyId] = rsa.ExportPkcs8PrivateKeyPem(),
        });
        var translationService = new Mock<ITranslationService>();
        translationService.SetupGet(x => x.CurrentLanguageModel)
            .Returns(new AppLanguageModel(AppLanguage.Turkish, "Turkish", "tr-TR"));
        var handler = new IntelligenceRequestHandler(userId, mailboxId, decryptor);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var client = new WinoAccountApiClient(
            database,
            httpClient,
            encryptor,
            translationService.Object);

        var search = await client.SearchIntelligenceAsync(new IntelligenceSemanticSearchRequest(
            "Atlas projesi",
            [new IntelligenceMailboxSearchScopeDto(mailboxId)],
            7,
            0.45,
            "Europe/Warsaw",
            "tr-TR",
            true));
        var briefing = await client.GetDailyBriefingAsync(
            mailboxId,
            new DateOnly(2026, 8, 10),
            "Europe/Warsaw",
            forceRegenerate: true);
        var deadlines = await client.GetDeadlinesAsync(
            mailboxId,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var suggestedRequestId = Guid.NewGuid();
        var suggestedReplies = await client.GetSuggestedRepliesAsync(
            mailboxId,
            new WinoSuggestedRepliesRequest(
                new WinoSuggestedReplyMessage(
                    "m09",
                    new string('a', 64),
                    "Ticket 7719",
                    "support@example.test",
                    "Please send the application version and a screenshot.",
                    DateTimeOffset.UtcNow,
                    IsOutgoing: false),
                [],
                [],
                "neutral",
                2),
            suggestedRequestId);

        search.Items.Should().ContainSingle().Which.RemoteMessageId.Should().Be("server-m07");
        briefing.Language.Should().Be("tr-TR");
        briefing.Items.Should().ContainSingle().Which.Headline.Should().Be("Bugünkü önemli ileti");
        deadlines.Should().ContainSingle().Which.RemoteMessageId.Should().Be("m19");
        suggestedReplies.Language.Should().Be("tr-TR");
        suggestedReplies.Suggestions.Should().HaveCount(2);
        suggestedReplies.RequestId.Should().Be(suggestedRequestId);
        handler.RequestCount.Should().Be(4);
    }

    private sealed class IntelligenceRequestHandler(
        Guid userId,
        Guid mailboxId,
        IContentEnvelopeDecryptor decryptor) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            request.Headers.Authorization?.Scheme.Should().Be("Bearer");
            request.Headers.Authorization?.Parameter.Should().Be("access-token");
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                request.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var envelope = ContentEnvelopeBinaryCodec.Decode(bytes, 256 * 1024);
                var plaintext = decryptor.Decrypt(envelope, new ContentEnvelopeContext(userId, Guid.Empty, "/api/v1/ai/intelligence/search"));
                using var body = JsonDocument.Parse(plaintext);
                body.RootElement.GetProperty("Query").GetString().Should().Be("Atlas projesi");
                body.RootElement.GetProperty("Limit").GetInt32().Should().Be(7);
                body.RootElement.GetProperty("MaximumDistance").GetDouble().Should().Be(0.45);
                body.RootElement.GetProperty("TimeZoneId").GetString().Should().Be("Europe/Warsaw");
                body.RootElement.GetProperty("Language").GetString().Should().Be("tr-TR");
                body.RootElement.GetProperty("UseQueryPlanner").GetBoolean().Should().BeTrue();
                return Json("""
                    {"isSuccess":true,"result":{"items":[{"mailboxId":"__MAILBOX_ID__","remoteMessageId":"server-m07","similarity":0.91}],"mailboxes":[{"mailboxId":"__MAILBOX_ID__","state":"current","omissionReason":null}]}}
                    """.Replace("__MAILBOX_ID__", mailboxId.ToString("D"), StringComparison.Ordinal));
            }

            if (path.EndsWith("/daily-briefing", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                body.RootElement.GetProperty("Language").GetString().Should().Be("tr-TR");
                body.RootElement.GetProperty("ForceRegenerate").GetBoolean().Should().BeTrue();
                return Json("""
                    {"isSuccess":true,"result":{"mailboxId":"__MAILBOX_ID__","localDate":"2026-08-10","timeZoneId":"Europe/Warsaw","language":"tr-TR","throughArtifactRevision":12,"generatedAtUtc":"2026-08-10T07:00:00Z","items":[{"remoteMessageId":"m16","section":"important","headline":"Bugünkü önemli ileti","action":"Yanıtla","dueAtUtc":null,"confidence":0.9}]}}
                    """.Replace("__MAILBOX_ID__", mailboxId.ToString("D"), StringComparison.Ordinal));
            }

            if (path.EndsWith("/deadlines", StringComparison.Ordinal))
            {
                request.RequestUri.Query.Should().Contain("dueAfterUtc=");
                request.RequestUri.Query.Should().Contain("dueBeforeUtc=");
                return Json("""
                    {"isSuccess":true,"result":[{"remoteMessageId":"m19","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","deadlineKind":"payment","dueAtUtc":"2026-08-17T00:00:00Z","localDate":"2026-08-17","timeZoneId":"UTC","precision":"date","action":"pay","status":"open","confidence":0.95,"generatedAtUtc":"2026-08-10T07:00:00Z"}]}
                    """);
            }

            if (path.EndsWith("/suggested-replies", StringComparison.Ordinal))
            {
                request.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var envelope = ContentEnvelopeBinaryCodec.Decode(bytes, 2 * 1024 * 1024);
                var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/suggested-replies";
                var plaintext = decryptor.Decrypt(envelope, new ContentEnvelopeContext(userId, mailboxId, route));
                try
                {
                    using var body = JsonDocument.Parse(plaintext);
                    body.RootElement.GetProperty("Language").GetString().Should().Be("tr-TR");
                    body.RootElement.GetProperty("Count").GetInt32().Should().Be(2);
                    body.RootElement.GetProperty("Target").GetProperty("Body").GetString()
                        .Should().Contain("application version");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                return Json("""
                    {"isSuccess":true,"result":{"requestId":"__REQUEST_ID__","language":"tr-TR","suggestions":[{"text":"Sürüm bilgisini paylaşacağım.","tone":"neutral"},{"text":"Ekran görüntüsünü de ekleyeceğim.","tone":"neutral"}],"retrievedContextMessageIds":[],"generationProfileId":"test-model"}}
                    """.Replace("__REQUEST_ID__", envelope.RequestId.ToString("D"), StringComparison.Ordinal));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
