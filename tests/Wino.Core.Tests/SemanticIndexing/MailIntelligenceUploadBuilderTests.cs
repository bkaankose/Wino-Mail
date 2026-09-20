using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using SQLite;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

/// <summary>
/// The encrypted SQLite upload one job is submitted as.
/// </summary>
public sealed class MailIntelligenceUploadBuilderTests
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEApwRDYSXuiybl8qzU8mTi
        uRuYRZrh/+9F3y6ncZ9KGUcs9ht1leqYfd6gecG4/lawB3LsRWNYco/qswpkcFb/
        Wlf+em4bdu1nDykRnPrHv+x1Dn8LqnIQZ1M1+OMP7+2db7qw8EUuBWJ00LSJ/q58
        I1O1jjstUtxRVj3P1Ei3Goau5IhzVfhSZAlApRXM6DvX/6exVQoKI/F9KXX5tAVV
        Bev4tsQ19Fz6O9QcJh8wm6zYL3vC3CT7e1LtmR/PEQIrgcIgZOJk+6LShbatIuYP
        1mBReiMbUqpoUOaooN/Qi+0y6HOqlOLEPODdiY6Qd9qsGGnw/MWX+z1pP/bqfOOs
        gO0tb2OTNEtQ4kPOIVDL+wzLYcbcnbbNjlQ5UUATHpjNBgPj+u+eaTRLqpdrl4O3
        xYVnA4mVKpYqMgCrvHkPETvcNW9fjU7pA7hiubikht3zorV5o+NyblFXPR8K+oiy
        egIlAcMu0ngv3/x9EaRE1o6VfSq7rantZU0zMGp2j84xAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid MailboxId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    private static MailIntelligenceUploadBuilder CreateBuilder()
        => new(new PemContentEnvelopeEncryptor(
            new ContentEncryptionPublicKey(EmbeddedIntelligencePublicKeyProvider.KeyId, PublicKey)));

    [Fact]
    public void Build_WritesAManifestMatchingTheJob()
    {
        var jobId = Guid.NewGuid();
        var upload = CreateBuilder().Build(UserId, MailboxId, jobId, [Message("m1"), Message("m2")], "en-US");

        var path = WriteToDisk(upload.Content);
        try
        {
            using var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
            var manifest = connection.Query<ManifestRow>("SELECT * FROM manifest").Single();

            manifest.format_version.Should().Be(MailIntelligenceFormatVersions.Current);
            manifest.job_id.Should().Be(jobId.ToString("D"));
            manifest.mailbox_id.Should().Be(MailboxId.ToString("D"));
            manifest.message_count.Should().Be(2);
            manifest.language.Should().Be("en-US");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_StoresEveryMessageAsAnOrderedEncryptedEnvelope()
    {
        var upload = CreateBuilder().Build(UserId, MailboxId, Guid.NewGuid(), [Message("m1"), Message("m2")], "en-US");

        var path = WriteToDisk(upload.Content);
        try
        {
            using var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
            var rows = connection.Query<MessageRow>("SELECT ordinal, envelope FROM messages ORDER BY ordinal");

            rows.Select(static row => row.ordinal).Should().Equal(0, 1);
            rows.Should().OnlyContain(row => row.envelope.Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Build_LeavesNoMailContentInPlaintext()
    {
        const string secret = "TOTALLY-SECRET-BODY-TEXT";
        var upload = CreateBuilder().Build(
            UserId, MailboxId, Guid.NewGuid(),
            [SecretMessage(secret)],
            "en-US");

        // The whole file is scanned, not just the message rows: nothing readable may leak.
        System.Text.Encoding.UTF8.GetString(upload.Content).Should().NotContain(secret);
    }

    [Fact]
    public void Build_ReportsTheChecksumOfTheBytesItReturns()
    {
        var upload = CreateBuilder().Build(UserId, MailboxId, Guid.NewGuid(), [Message("m1")], "en-US");

        upload.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(upload.Content)));
        upload.MessageCount.Should().Be(1);
    }

    [Fact]
    public void Build_RejectsAnEmptySelection()
    {
        var act = () => CreateBuilder().Build(UserId, MailboxId, Guid.NewGuid(), [], "en-US");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSingle_ProducesAStandaloneEnvelope()
    {
        var envelope = CreateBuilder().BuildSingle(UserId, MailboxId, Message("m1"));

        envelope.Should().NotBeEmpty();
        // The single-mail route binds a different path into the authenticated data, so the
        // envelope is not interchangeable with an upload row.
        MailIntelligenceUploadBuilder.EnvelopeRoute(MailboxId).Should().EndWith("/jobs");
    }

    private static MailIntelligenceUploadEnvelopeDto SecretMessage(string secret) => new()
    {
        RemoteMessageId = "m1",
        ContentHash = "hash-m1",
        Subject = secret,
        Sender = "sender@example.test",
        Body = secret,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        ProviderImportance = "normal",
        RemoteFolderIds = [],
    };

    private static MailIntelligenceUploadEnvelopeDto Message(string remoteMessageId) => new()
    {
        RemoteMessageId = remoteMessageId,
        ContentHash = $"hash-{remoteMessageId}",
        Subject = $"Subject {remoteMessageId}",
        Sender = "sender@example.test",
        Body = "Body text.",
        OccurredAtUtc = DateTimeOffset.UtcNow,
        ProviderImportance = "normal",
        RemoteFolderIds = [],
    };

    private static string WriteToDisk(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wino-upload-test-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class ManifestRow
    {
        public int format_version { get; set; }
        public string job_id { get; set; } = string.Empty;
        public string mailbox_id { get; set; } = string.Empty;
        public string key_id { get; set; } = string.Empty;
        public string created_utc { get; set; } = string.Empty;
        public int message_count { get; set; }
        public string language { get; set; } = string.Empty;
    }

    private sealed class MessageRow
    {
        public int ordinal { get; set; }
        public byte[] envelope { get; set; } = [];
    }
}
