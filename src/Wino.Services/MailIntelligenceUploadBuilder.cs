#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLite;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;

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

    /// <summary>
    /// Whether the message carried a List-Unsubscribe header. Sent as a fact so the server
    /// never has to guess at it, and so an Unsubscribe button is only ever offered for a
    /// message that has something to unsubscribe from.
    /// </summary>
    public bool HasListUnsubscribe { get; init; }
}

[JsonSerializable(typeof(MailIntelligenceUploadEnvelopeDto))]
internal sealed partial class MailIntelligenceUploadJsonContext : JsonSerializerContext;

public sealed record MailIntelligenceUpload(Guid JobId, byte[] Content, string Sha256, int MessageCount);

/// <summary>
/// Builds the encrypted SQLite upload for one job. No mail content is ever plaintext in
/// the file: every message row holds an envelope encrypted to the server transport key and
/// bound to the user, the mailbox and the upload route. The manifest names the device result
/// key, so the server can encrypt the results to this device and to nobody else.
/// </summary>
public sealed class MailIntelligenceUploadBuilder
{
    /// <summary>
    /// Version 2 adds result_key_id and result_public_key to the manifest.
    /// TODO: use MailIntelligenceFormatVersions.Current once Contracts 3.0.0-alpha.1 ships.
    /// </summary>
    public const int FormatVersion = 2;

    /// <summary>Route bound into the envelope authenticated data. Must match the server.</summary>
    public static string EnvelopeRoute(Guid mailboxId)
        => $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/jobs";

    public MailIntelligenceUpload Build(
        Guid winoUserId,
        Guid mailboxId,
        Guid jobId,
        IReadOnlyList<MailIntelligenceUploadEnvelopeDto> messages,
        string language,
        ContentEncryptionPublicKey transportKey,
        IntelligenceResultKey resultKey)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(transportKey);
        ArgumentNullException.ThrowIfNull(resultKey);
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
                    " language TEXT NOT NULL," +
                    " result_key_id TEXT NOT NULL," +
                    " result_public_key TEXT NOT NULL)");
                connection.Execute(
                    "CREATE TABLE messages (" +
                    " ordinal INTEGER PRIMARY KEY," +
                    " envelope BLOB NOT NULL)");

                connection.Execute(
                    "INSERT INTO manifest VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    FormatVersion,
                    jobId.ToString("D"),
                    mailboxId.ToString("D"),
                    transportKey.KeyId,
                    DateTimeOffset.UtcNow.ToString("O"),
                    messages.Count,
                    language,
                    resultKey.KeyId,
                    resultKey.PublicKeyPem);

                var encryptor = new PemContentEnvelopeEncryptor(transportKey);
                var route = EnvelopeRoute(mailboxId);
                connection.RunInTransaction(() =>
                {
                    for (var index = 0; index < messages.Count; index++)
                    {
                        var envelope = Encrypt(encryptor, winoUserId, mailboxId, route, messages[index]);
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
            return new MailIntelligenceUpload(jobId, content, Convert.ToHexStringLower(SHA256.HashData(content)), messages.Count);
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

    private static byte[] Encrypt(
        IContentEnvelopeEncryptor encryptor,
        Guid winoUserId,
        Guid mailboxId,
        string route,
        MailIntelligenceUploadEnvelopeDto message)
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
    public byte[] BuildSingle(
        Guid winoUserId,
        Guid mailboxId,
        MailIntelligenceUploadEnvelopeDto message,
        ContentEncryptionPublicKey transportKey)
        => Encrypt(
            new PemContentEnvelopeEncryptor(transportKey),
            winoUserId,
            mailboxId,
            $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/messages:analyze",
            message);
}
