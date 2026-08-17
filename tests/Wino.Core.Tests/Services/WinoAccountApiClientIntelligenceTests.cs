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
    public async Task GetIntelligenceArtifactsAsync_DefaultsOmittedDeletionMarkerToFalse()
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

        using var httpClient = new HttpClient(new CompactArtifactRequestHandler())
        {
            BaseAddress = new Uri("https://api.example.test/"),
        };
        using var client = new WinoAccountApiClient(database, httpClient);

        var page = await client.GetIntelligenceArtifactsAsync(mailboxId, null, 100);

        page.NextCursor.Should().BeNull();
        var artifact = page.Items.Should().ContainSingle().Subject;
        artifact.RemoteMessageId.Should().Be("message-1");
        artifact.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task IngestIntelligenceAsync_DefaultsOmittedDeletionMarkerToFalse()
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

        using var httpClient = new HttpClient(new CompactArtifactRequestHandler())
        {
            BaseAddress = new Uri("https://api.example.test/"),
        };
        using var client = new WinoAccountApiClient(database, httpClient);

        var result = await client.IngestIntelligenceAsync(mailboxId, [1, 2, 3]);

        result.Items.Should().ContainSingle().Which.RemoteMessageId.Should().Be("message-1");
        result.Artifacts.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveIntelligenceDeltaAsync_SendsIdsWithoutCoverageQuery()
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

        var handler = new DeltaRangeRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var client = new WinoAccountApiClient(database, httpClient);

        await client.ResolveIntelligenceDeltaAsync(mailboxId, ["message-1", "message-2"]);

        handler.RequestUri.Should().NotBeNull();
        var query = Uri.UnescapeDataString(handler.RequestUri!.Query);
        query.Should().BeEmpty();
    }

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
        var translated = await client.TranslateBriefingHeadlinesAsync(mailboxId, "tr-TR");
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
        translated.HeadlineLanguage.Should().Be("tr-TR");
        translated.Headlines.Should().ContainSingle().Which.Headline.Should().Be("Bugünkü önemli ileti");
        suggestedReplies.Language.Should().Be("tr-TR");
        suggestedReplies.Suggestions.Should().HaveCount(2);
        suggestedReplies.RequestId.Should().Be(suggestedRequestId);
        handler.RequestCount.Should().Be(3);
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

            if (path.EndsWith("/headlines:translate", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                body.RootElement.GetProperty("TargetLanguage").GetString().Should().Be("tr-TR");
                return Json("""
                    {"isSuccess":true,"result":{"headlineLanguage":"tr-TR","translatedCount":1,"failedCount":0,"throughArtifactRevision":12,"headlines":[{"briefingId":"11111111-1111-1111-1111-111111111111","headline":"Bugünkü önemli ileti","artifactRevision":12,"updatedAtUtc":"2026-08-10T07:00:00Z"}]}}
                    """.Replace("__MAILBOX_ID__", mailboxId.ToString("D"), StringComparison.Ordinal));
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

    private sealed class CompactArtifactRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization?.Scheme.Should().Be("Bearer");
            var isIngest = request.RequestUri!.AbsolutePath.EndsWith("/ingest", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isIngest
                    ? """
                      {"isSuccess":true,"result":{"items":[{"remoteMessageId":"message-1","status":"indexed","errorCode":null}],"artifacts":[{"remoteMessageId":"message-1","contentHash":"hash-1","capability":"smartLabels","generationVersion":1,"payloadSchemaVersion":1,"artifactRevision":5,"generatedAtUtc":"2026-08-12T10:00:00Z"}]}}
                      """
                    : """
                      {"isSuccess":true,"result":{"nextCursor":null,"items":[{"remoteMessageId":"message-1","contentHash":"hash-1","capability":"smartLabels","generationVersion":1,"payloadSchemaVersion":1,"artifactRevision":5,"generatedAtUtc":"2026-08-12T10:00:00Z"}]}}
                      """, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class DeltaRangeRequestHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sequence\":0,\"isFinal\":true,\"missingRemoteMessageIds\":[]}\n",
                    Encoding.UTF8,
                    "application/x-ndjson"),
            });
        }
    }
}
