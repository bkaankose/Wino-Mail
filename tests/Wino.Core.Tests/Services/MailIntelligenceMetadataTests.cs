using FluentAssertions;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MailIntelligenceMetadataTests
{
    [Fact]
    public void CreateIntelligenceMetadata_ProjectsDownloadedV1LabelsAndHeadline()
    {
        var document = Document(
            "A useful headline",
            new SmartLabelScoreV1(SmartLabelV1.Important, 0.91),
            new SmartLabelScoreV1(SmartLabelV1.Travel, 0.82));

        var result = MailService.CreateIntelligenceMetadata(
            "message-1",
            document);

        result.Should().NotBeNull();
        result!.Headline.Should().Be("A useful headline");
        result.Summary.Should().Be("summary");
        result.SmartLabels.Should().BeEquivalentTo(
        [
            new SmartLabelScore(MailSmartLabel.Important, 0.91),
            new SmartLabelScore(MailSmartLabel.Travel, 0.82),
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void CreateIntelligenceMetadata_DeduplicatesV1Labels()
    {
        var document = Document(
            string.Empty,
            new SmartLabelScoreV1(SmartLabelV1.Important, 0.99),
            new SmartLabelScoreV1(SmartLabelV1.Important, 0.75),
            new SmartLabelScoreV1(SmartLabelV1.Action, 0.88));

        var result = MailService.CreateIntelligenceMetadata(
            "message-1",
            document);

        result!.SmartLabels.Select(static label => label.Label).Should().Equal(
            MailSmartLabel.Important,
            MailSmartLabel.Action);
    }

    private static MessageIntelligenceDownloadDto Document(
        string headline,
        params SmartLabelScoreV1[] labels)
        => new()
        {
            ServerMessageKey = "message-1",
            ContentHash = "hash",
            Subject = "subject",
            Sender = "sender@example.test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IsOutgoing = false,
            IsRead = false,
            IsFlagged = false,
            HasAttachments = false,
            FolderIds = ["inbox"],
            SenderAddresses = ["sender@example.test"],
            RecipientAddresses = ["user@example.test"],
            Analysis = new MessageIntelligenceDocumentV1
            {
                SourceLanguage = "en",
                Headline = headline,
                Summary = "summary",
                Category = MessageCategoryV1.Conversation,
                Intent = MessageIntentV1.Inform,
                Urgency = MessageUrgencyV1.Normal,
                Confidence = 0.9,
                SmartLabels = labels,
            },
            Embedding = Convert.ToBase64String(new byte[3_072]),
            EmbeddingDimensions = 768,
            EmbeddingEncoding = "float32-le",
            ArtifactRevision = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
}
