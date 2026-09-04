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
    public async Task AcceptIntelligenceConsentAsync_SendsCurrentPolicyAndAllowedSource()
    {
        var userId = Guid.NewGuid();
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = userId,
            Email = "consent@example.test",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });

        using var httpClient = new HttpClient(new ConsentRequestHandler())
        {
            BaseAddress = new Uri("https://api.example.test/"),
        };
        using var client = new WinoAccountApiClient(database, httpClient);

        var result = await client.AcceptIntelligenceConsentAsync("2026-08-15", ConsentActionSources.ConsentPage);

        result.Status.Should().Be(ConsentStatuses.Active);
        result.AcceptedPolicyVersion.Should().Be("2026-08-15");
    }

    [Fact]
    public async Task V1Lifecycle_UsesExactRoutesBodiesAndResponseContracts()
    {
        var userId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = userId,
            Email = "intelligence@example.test",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });

        var handler = new V1LifecycleRequestHandler(mailboxId, epoch);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var client = new WinoAccountApiClient(database, httpClient);

        var manifest = await client.GetWinoIntelligenceManifestAsync();
        var head = await client.GetIntelligenceHeadAsync(mailboxId);
        var begin = await client.BeginIntelligenceReindexAsync(
            mailboxId,
            new BeginIntelligenceReindexRequest(WinoIntelligenceVersions.V1, Guid.Parse("11111111-1111-1111-1111-111111111111")));
        var accepted = await client.StartMessageIntelligenceIngestionJobAsync(mailboxId, [4, 5, 6]);
        var job = await client.GetMessageIntelligenceIngestionJobAsync(mailboxId, accepted.JobId);
        var ingest = await client.IngestMessageIntelligenceAsync(mailboxId, [1, 2, 3]);
        var reconcile = await client.ReconcileMessageIntelligenceAsync(
            mailboxId,
            new ReconcileMessageIntelligenceRequest(WinoIntelligenceVersions.V1, epoch, ["m1", "m2"]));
        var changes = await client.GetIntelligenceChangesAsync(
            mailboxId,
            WinoIntelligenceVersions.V1,
            epoch,
            4,
            250);

        manifest.LatestIntelligenceVersion.Should().Be(WinoIntelligenceVersions.V1);
        head.Should().NotBeNull();
        head!.IndexEpoch.Should().Be(epoch);
        begin.IndexEpoch.Should().Be(epoch);
        accepted.TotalCount.Should().Be(1);
        job.Status.Should().Be(MessageIntelligenceIngestionJobStatuses.Completed);
        ingest.Items.Should().ContainSingle().Which.Status.Should().Be("indexed");
        reconcile.MissingServerMessageKeys.Should().Equal("m2");
        changes.Items.Should().ContainSingle().Which.Document!.EmbeddingDimensions.Should().Be(768);
        handler.RequestCount.Should().Be(8);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task StartIngestionJob_DoesNotRetryARejectedRequest(HttpStatusCode statusCode)
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
        var handler = new RejectingJobStartHandler(statusCode);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var client = new WinoAccountApiClient(database, httpClient, maximumEncryptedAttempts: 5);

        var action = () => client.StartMessageIntelligenceIngestionJobAsync(mailboxId, [1, 2, 3]);

        await action.Should().ThrowAsync<InvalidOperationException>();
        handler.RequestCount.Should().Be(1);
    }

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

    private sealed class V1LifecycleRequestHandler(Guid mailboxId, Guid epoch) : HttpMessageHandler
    {
        private readonly Guid _jobId = Guid.NewGuid();
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            request.Headers.Authorization?.Parameter.Should().Be("access-token");
            var path = request.RequestUri!.AbsolutePath;
            var mailboxRoot = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}";

            if (path == "/api/v1/ai/intelligence/manifest")
            {
                request.Method.Should().Be(HttpMethod.Get);
                return Json("""{"isSuccess":true,"result":{"latestIntelligenceVersion":"wino-intelligence-v1","supportedIntelligenceVersions":["wino-intelligence-v1"]}}""");
            }

            if (path == $"{mailboxRoot}/head")
            {
                request.Method.Should().Be(HttpMethod.Get);
                return Json(HeadJson());
            }

            if (path == $"{mailboxRoot}/reindex:begin")
            {
                request.Method.Should().Be(HttpMethod.Post);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                body.RootElement.GetProperty("TargetIntelligenceVersion").GetString().Should().Be(WinoIntelligenceVersions.V1);
                body.RootElement.GetProperty("OperationId").GetGuid().Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                return Json($$$"""{"isSuccess":true,"result":{"mailboxId":"{{{mailboxId:D}}}","intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","replayed":false}}""");
            }

            if (path == $"{mailboxRoot}/ingest")
            {
                request.Method.Should().Be(HttpMethod.Post);
                request.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
                (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(1, 2, 3);
                return Json($$$"""{"isSuccess":true,"result":{"intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","items":[{"serverMessageKey":"m1","status":"indexed","errorCode":null,"artifactRevision":5}]}}""");
            }

            if (path == $"{mailboxRoot}/ingestion-jobs")
            {
                request.Method.Should().Be(HttpMethod.Post);
                request.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
                (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Should().Equal(4, 5, 6);
                return Json($$$"""{"isSuccess":true,"result":{"jobId":"{{{_jobId:D}}}","intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","totalCount":1}}""");
            }

            if (path == $"{mailboxRoot}/ingestion-jobs/{_jobId:D}")
            {
                request.Method.Should().Be(HttpMethod.Get);
                return Json($$$"""{"isSuccess":true,"result":{"jobId":"{{{_jobId:D}}}","intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","status":"completed","totalCount":1,"completedCount":1,"indexedCount":1,"alreadyIndexedCount":0,"failedCount":0,"items":[{"serverMessageKey":"m1","status":"indexed","errorCode":null,"artifactRevision":5}]}}""");
            }

            if (path == $"{mailboxRoot}/reconcile")
            {
                request.Method.Should().Be(HttpMethod.Post);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                body.RootElement.GetProperty("IntelligenceVersion").GetString().Should().Be(WinoIntelligenceVersions.V1);
                body.RootElement.GetProperty("IndexEpoch").GetGuid().Should().Be(epoch);
                body.RootElement.GetProperty("ServerMessageKeys").EnumerateArray().Select(static item => item.GetString()).Should().Equal("m1", "m2");
                return Json($$$"""{"isSuccess":true,"result":{"intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","coveredServerMessageKeys":["m1"],"missingServerMessageKeys":["m2"]}}""");
            }

            if (path == $"{mailboxRoot}/changes")
            {
                request.Method.Should().Be(HttpMethod.Get);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                query.Should().Be($"?intelligenceVersion={WinoIntelligenceVersions.V1}&indexEpoch={epoch:D}&afterRevision=4&pageSize=250");
                return Json(ChangesJson());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string HeadJson()
            => $$$"""{"isSuccess":true,"result":{"mailboxId":"{{{mailboxId:D}}}","intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","artifactRevision":5,"indexedMessageCount":1,"storageSizeBytes":3072,"oldestAnalyzedMessageUtc":"2026-09-01T10:00:00Z","newestAnalyzedMessageUtc":"2026-09-01T10:00:00Z","createdAtUtc":"2026-09-01T10:00:00Z","updatedAtUtc":"2026-09-01T10:00:00Z"}}""";

        private string ChangesJson()
            => $$$"""{"isSuccess":true,"result":{"intelligenceVersion":"wino-intelligence-v1","indexEpoch":"{{{epoch:D}}}","throughRevision":5,"nextAfterRevision":5,"items":[{"revision":5,"serverMessageKey":"m1","isDeleted":false,"changedAtUtc":"2026-09-01T10:00:00Z","document":{"serverMessageKey":"m1","contentHash":"hash","subject":"subject","sender":"sender@example.test","receivedAtUtc":"2026-09-01T10:00:00Z","isOutgoing":false,"isRead":true,"isFlagged":false,"hasAttachments":false,"folderIds":["inbox"],"senderAddresses":["sender@example.test"],"recipientAddresses":["user@example.test"],"analysis":{"sourceLanguage":"en","headline":"headline","summary":"summary","category":"conversation","intent":"inform","urgency":"normal","confidence":1,"smartLabels":[],"topics":[],"entities":[],"documents":[],"actions":[],"temporalReferences":[],"anomalies":[]},"embedding":"{{{Convert.ToBase64String(new byte[3072])}}}","embeddingDimensions":768,"embeddingEncoding":"float32-le","artifactRevision":5,"generatedAtUtc":"2026-09-01T10:00:00Z"}}]}}""";

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ConsentRequestHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/ai/consent");
            request.Headers.Authorization?.Parameter.Should().Be("access-token");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
            body.RootElement.GetProperty("PolicyVersion").GetString().Should().Be("2026-08-15");
            body.RootElement.GetProperty("Source").GetString().Should().Be(ConsentActionSources.ConsentPage);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"isSuccess":true,"result":{"status":"active","currentPolicyVersion":"2026-08-15","acceptedPolicyVersion":"2026-08-15","acceptedAtUtc":"2026-09-03T12:00:00Z","revokedAtUtc":null,"privacyPolicyUrl":"https://www.winomail.app/privacy","dataDeletionStatus":"notRequired"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class RejectingJobStartHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().EndWith("/ingestion-jobs");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    """{"isSuccess":false,"errorCode":"INTELLIGENCE_INGESTION_QUEUE_FULL"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
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
