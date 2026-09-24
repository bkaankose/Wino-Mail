using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// Result pages of a keyed job arrive as an encrypted page the reader checks and opens; pages
/// of a job without a key arrive plain.
/// </summary>
public sealed class MailIntelligenceResultPageReaderTests
{
    private const string KeyId = "dev-202609-0123abcd";
    private const string InnerPageJson = """{"formatVersion":2,"pageIndex":3,"pageCount":0,"items":[],"failures":[],"digest":""}""";

    private static readonly Guid MailboxId = Guid.Parse("f1f2f3f4-0102-0304-0506-070809101112");
    private static readonly Guid JobId = Guid.Parse("0a0b0c0d-1111-2222-3333-444455556666");
    private static readonly byte[] Envelope = [1, 2, 3, 4, 5];

    private readonly Mock<IWinoAccountApiClient> _api = new();
    private readonly Mock<IIntelligenceResultKeyStore> _keys = new();

    private MailIntelligenceResultPageReader CreateReader() => new(_api.Object, _keys.Object);

    [Fact]
    public async Task KeyedJob_OpensTheEnvelopeWithThePaddedPageRoute()
    {
        ServePage(Encrypted(MailIntelligenceStageIds.Classification, 3, KeyId));
        string? route = null;
        _keys.Setup(x => x.DecryptAsync(KeyId, It.IsAny<ReadOnlyMemory<byte>>(), MailboxId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, ReadOnlyMemory<byte>, Guid, string, CancellationToken>((_, _, _, r, _) => route = r)
            .ReturnsAsync(() => Encoding.UTF8.GetBytes(InnerPageJson));

        var read = await CreateReader().ReadClassificationAsync(Job(KeyId), 3, CancellationToken.None);

        route.Should().Be($"/api/v2/ai/intelligence/mailboxes/{MailboxId:D}/jobs/{JobId:D}/results/classification/page-00003");
        read.Page.PageIndex.Should().Be(3);
        read.EnvelopeHash.Should().Be(MailIntelligenceResultDigest.PageHash(Envelope));
    }

    [Fact]
    public async Task KeyedJob_RefusesAPlainPage()
    {
        ServePage(Encoding.UTF8.GetBytes(InnerPageJson));

        var act = () => CreateReader().ReadClassificationAsync(Job(KeyId), 3, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        VerifyNeverDecrypted();
    }

    [Theory]
    [InlineData("classification", 3, "dev-202609-ffffffff")]
    [InlineData("classification", 4, KeyId)]
    [InlineData("enrichment", 3, KeyId)]
    public async Task KeyedJob_RefusesAPageMeantForAnotherKeyStageOrIndex(string stage, int pageIndex, string keyId)
    {
        ServePage(Encrypted(stage, pageIndex, keyId));

        var act = () => CreateReader().ReadClassificationAsync(Job(KeyId), 3, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        VerifyNeverDecrypted();
    }

    [Fact]
    public async Task JobWithoutKey_ReadsThePlainPage()
    {
        ServePage(Encoding.UTF8.GetBytes(InnerPageJson));

        var read = await CreateReader().ReadClassificationAsync(Job(resultKeyId: null), 3, CancellationToken.None);

        read.Page.PageIndex.Should().Be(3);
        read.EnvelopeHash.Should().BeNull();
        VerifyNeverDecrypted();
    }

    [Fact]
    public void DigestCheck_PassesOnlyForTheServersDigestOverCiphertext()
    {
        var hashes = new[] { MailIntelligenceResultDigest.PageHash([1]), MailIntelligenceResultDigest.PageHash([2]) };
        var digest = MailIntelligenceResultDigest.Compute(hashes);

        var matches = () => MailIntelligenceCoordinator.EnsureDigestMatches(Job(KeyId), "classification", hashes, digest);
        var reordered = () => MailIntelligenceCoordinator.EnsureDigestMatches(Job(KeyId), "classification", [hashes[1], hashes[0]], digest);
        var missing = () => MailIntelligenceCoordinator.EnsureDigestMatches(Job(KeyId), "classification", [hashes[0]], digest);
        var legacy = () => MailIntelligenceCoordinator.EnsureDigestMatches(Job(resultKeyId: null), "classification", [null], "anything");

        matches.Should().NotThrow();
        reordered.Should().Throw<InvalidOperationException>();
        missing.Should().Throw<InvalidOperationException>();
        legacy.Should().NotThrow("jobs without a key keep the server's own item digest");
    }

    private void ServePage(byte[] content)
        => _api.Setup(x => x.GetMailIntelligenceResultPageAsync(MailboxId, JobId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);

    private void VerifyNeverDecrypted()
        => _keys.Verify(x => x.DecryptAsync(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    private static byte[] Encrypted(string stage, int pageIndex, string keyId)
        => JsonSerializer.SerializeToUtf8Bytes(
            new EncryptedResultPageDto(2, stage, pageIndex, 5, "digest", keyId, Envelope),
            WinoAccountApiJsonContext.Default.EncryptedResultPageDto);

    private static MailIntelligenceJobState Job(string? resultKeyId)
    {
        var stage = new MailIntelligenceStageState("published", 5, null, false, false);
        return new MailIntelligenceJobState(
            JobId, Guid.NewGuid(), MailboxId, 10, "completed", stage, stage, 0, null, DateTime.UtcNow, DateTime.UtcNow)
        {
            ResultKeyId = resultKeyId,
        };
    }
}
