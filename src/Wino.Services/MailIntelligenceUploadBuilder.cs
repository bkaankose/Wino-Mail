#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLite;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

/// <summary>
/// Plaintext inside one message envelope. Must match the server's upload envelope shape.
/// </summary>
public sealed class MailIntelligenceUploadEnvelopeDto
{
    public string RemoteMessageId { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string? Subject { get; init; }
    public string? Sender { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public bool IsOutgoing { get; init; }
    public bool IsDirectRecipient { get; init; }
    public bool HasLaterOutgoingReply { get; init; }
    public string? ProviderImportance { get; init; }
    public IReadOnlyList<string>? RemoteFolderIds { get; init; }
}

[JsonSerializable(typeof(MailIntelligenceUploadEnvelopeDto))]
internal sealed partial class MailIntelligenceUploadJsonContext : JsonSerializerContext;

public sealed record MailIntelligenceUpload(byte[] Content, string Sha256, int MessageCount);

/// <summary>
/// Builds the encrypted SQLite upload for one job. No mail content is ever plaintext in
/// the file: every message row holds an encrypted envelope bound to the user, the mailbox
/// and the upload route.
/// </summary>
public sealed class MailIntelligenceUploadBuilder(IContentEnvelopeEncryptor encryptor)
{
    /// <summary>Route bound into the envelope authenticated data. Must match the server.</summary>
    public static string EnvelopeRoute(Guid mailboxId)
        => $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/jobs";

    public MailIntelligenceUpload Build(
        Guid winoUserId,
        Guid mailboxId,
        Guid jobId,
        IReadOnlyList<MailIntelligenceUploadEnvelopeDto> messages,
        string language)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("An upload needs at least one message.", nameof(messages));
        }

        var path = Path.Combine(Path.GetTempPath(), $"wino-intelligence-upload-{jobId:N}.db");
        try
        {
            using (var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create))
            {
                connection.Execute(
                    "CREATE TABLE manifest (" +
                    " format_version INTEGER NOT NULL," +
                    " job_id TEXT NOT NULL," +
                    " mailbox_id TEXT NOT NULL," +
                    " key_id TEXT NOT NULL," +
                    " created_utc TEXT NOT NULL," +
                    " message_count INTEGER NOT NULL," +
                    " language TEXT NOT NULL)");
                connection.Execute(
                    "CREATE TABLE messages (" +
                    " ordinal INTEGER PRIMARY KEY," +
                    " envelope BLOB NOT NULL)");

                connection.Execute(
                    "INSERT INTO manifest VALUES (?, ?, ?, ?, ?, ?, ?)",
                    MailIntelligenceFormatVersions.Current,
                    jobId.ToString("D"),
                    mailboxId.ToString("D"),
                    EmbeddedIntelligencePublicKeyProvider.KeyId,
                    DateTimeOffset.UtcNow.ToString("O"),
                    messages.Count,
                    language);

                var route = EnvelopeRoute(mailboxId);
                connection.RunInTransaction(() =>
                {
                    for (var index = 0; index < messages.Count; index++)
                    {
                        var envelope = Encrypt(winoUserId, mailboxId, route, messages[index]);
                        try
                        {
                            connection.Execute("INSERT INTO messages VALUES (?, ?)", index, envelope);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(envelope);
                        }
                    }
                });
            }

            var content = File.ReadAllBytes(path);
            return new MailIntelligenceUpload(content, Convert.ToHexStringLower(SHA256.HashData(content)), messages.Count);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // A stale temp file is harmless; it is overwritten on the next build.
                }
            }
        }
    }

    private byte[] Encrypt(Guid winoUserId, Guid mailboxId, string route, MailIntelligenceUploadEnvelopeDto message)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            message, MailIntelligenceUploadJsonContext.Default.MailIntelligenceUploadEnvelopeDto);
        EncryptedContentEnvelope? envelope = null;
        try
        {
            envelope = encryptor.Encrypt(
                plaintext,
                new ContentEnvelopeContext(winoUserId, mailboxId, route),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow);
            return ContentEnvelopeBinaryCodec.Encode(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope.WrappedKey);
                CryptographicOperations.ZeroMemory(envelope.Nonce);
                CryptographicOperations.ZeroMemory(envelope.Tag);
                CryptographicOperations.ZeroMemory(envelope.Ciphertext);
            }
        }
    }

    /// <summary>
    /// Encrypts one message for the synchronous single-mail route, which binds a different
    /// route into the authenticated data.
    /// </summary>
    public byte[] BuildSingle(Guid winoUserId, Guid mailboxId, MailIntelligenceUploadEnvelopeDto message)
        => Encrypt(
            winoUserId,
            mailboxId,
            $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/messages:analyze",
            message);
}
