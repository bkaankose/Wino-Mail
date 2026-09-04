using System.Buffers.Binary;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LocalIntelligenceSearchEngineTests
{
    [Fact]
    public async Task FindSimilar_RanksCosineMatchesAndCanRestrictToOutgoingMail()
    {
        var accountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var store = Store(
            SearchDocument(accountId, mailboxId, "source", Vector(1, 0), false),
            SearchDocument(accountId, mailboxId, "best", Vector(0.95f, 0.05f), true),
            SearchDocument(accountId, mailboxId, "incoming", Vector(1, 0), false),
            SearchDocument(accountId, mailboxId, "unrelated", Vector(0, 1), true));

        var matches = await new LocalIntelligenceSearchEngine(store.Object)
            .FindSimilarAsync(accountId, "source", 10, outgoingOnly: true);

        matches.Select(static match => match.RemoteMessageId).Should().Equal("best");
        matches[0].Similarity.Should().BeGreaterThan(0.99);
    }

    [Fact]
    public async Task StructuredSearch_CorrelatesPredicatesToTheSameEntity()
    {
        var accountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var matching = SearchDocument(accountId, mailboxId, "matching", Vector(1, 0), false,
            [new("org", IntelligenceEntityTypeV1.Organization, "Acme", "Acme", IntelligenceEntityRoleV1.Issuer, 1)]);
        var splitAcrossEntities = SearchDocument(accountId, mailboxId, "split", Vector(1, 0), false,
            [
                new("org", IntelligenceEntityTypeV1.Organization, "Other Corp", "Other Corp", IntelligenceEntityRoleV1.Issuer, 1),
                new("person", IntelligenceEntityTypeV1.Person, "Acme", "Acme", IntelligenceEntityRoleV1.Mentioned, 1),
            ]);
        var store = Store(matching, splitAcrossEntities);
        var required = new[]
        {
            StringPredicate(SearchFieldV1.EntityType, "organization"),
            StringPredicate(SearchFieldV1.EntityName, "Acme"),
        };
        var plan = new SearchPlanV1
        {
            RetrievalMode = SearchRetrievalModeV1.Structured,
            Branches = [new SearchBooleanBranchV1(required, [], [])],
            Limit = 10,
        };
        var response = new IntelligenceSearchPlanResultDto(
            WinoIntelligenceVersions.V1,
            [],
            [new IntelligenceVersionSearchPlanDto(WinoIntelligenceVersions.V1, [mailboxId], plan, null, null, null, false)]);

        var matches = await new LocalIntelligenceSearchEngine(store.Object)
            .SearchAsync(response, [new LocalIntelligenceSearchScope(accountId, mailboxId, new HashSet<string>())], 10);

        matches.Select(static match => match.RemoteMessageId).Should().Equal("matching");
    }

    private static Mock<ILocalIntelligenceStore> Store(params LocalIntelligenceSearchDocument[] documents)
    {
        var store = new Mock<ILocalIntelligenceStore>();
        store.Setup(value => value.GetSearchDocumentsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid accountId, CancellationToken _) => documents.Where(document => document.LocalAccountId == accountId).ToArray());
        return store;
    }

    private static LocalIntelligenceSearchDocument SearchDocument(
        Guid accountId,
        Guid mailboxId,
        string key,
        byte[] vector,
        bool isOutgoing,
        IReadOnlyList<IntelligenceEntityV1>? entities = null)
        => new(accountId, mailboxId, new MessageIntelligenceDownloadDto
        {
            ServerMessageKey = key,
            ContentHash = "hash",
            Subject = key,
            Sender = "sender@example.test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IsOutgoing = isOutgoing,
            IsRead = false,
            IsFlagged = false,
            HasAttachments = false,
            FolderIds = ["inbox"],
            SenderAddresses = ["sender@example.test"],
            RecipientAddresses = ["recipient@example.test"],
            Analysis = new MessageIntelligenceDocumentV1
            {
                SourceLanguage = "en",
                Headline = key,
                Summary = key,
                Category = MessageCategoryV1.Conversation,
                Intent = MessageIntentV1.Inform,
                Urgency = MessageUrgencyV1.Normal,
                Confidence = 1,
                Entities = entities ?? [],
            },
            Embedding = Convert.ToBase64String(vector),
            EmbeddingDimensions = 768,
            EmbeddingEncoding = "float32-le",
            ArtifactRevision = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        }, vector);

    private static SearchPredicateV1 StringPredicate(SearchFieldV1 field, string value)
        => new()
        {
            Field = field,
            Operator = SearchOperatorV1.Equals,
            ValueType = SearchValueTypeV1.String,
            StringValue = value,
        };

    private static byte[] Vector(float first, float second)
    {
        var bytes = new byte[768 * sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, first);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(sizeof(float)), second);
        return bytes;
    }
}
