#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Whether the message carried a List-Unsubscribe header. Sent as a fact so the server
    /// never has to guess at it, and so an Unsubscribe button is only ever offered for a
    /// message that has something to unsubscribe from.
    /// </summary>
    public bool HasListUnsubscribe { get; init; }
}

[JsonSerializable(typeof(MailIntelligenceUploadEnvelopeDto))]
internal sealed partial class MailIntelligenceUploadJsonContext : JsonSerializerContext;

/// <summary>
/// Encrypts messages for the intelligence routes. No mail content leaves the device in
/// plaintext: every envelope is encrypted to the server transport key and bound to the user,
/// the mailbox and the route it is sent to.
/// </summary>
public sealed class MailIntelligenceEnvelopeBuilder
{
    /// <summary>
    /// Base64 of a binary envelope around <paramref name="plaintextBytes"/>, plus room for the
    /// wrapped key, nonce, tag and header, as it appears in a stream request.
    /// </summary>
    public static long EstimateRequestBytes(int plaintextBytes)
        => (((long)plaintextBytes + EnvelopeOverheadBytes + 2) / 3 * 4) + 4;

    private const int EnvelopeOverheadBytes = 2048;

    public static int MeasurePlaintext(MailIntelligenceUploadEnvelopeDto message)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            message, MailIntelligenceUploadJsonContext.Default.MailIntelligenceUploadEnvelopeDto);
        try
        {
            return plaintext.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Encrypts one batch for the streamed route. Called right before the request is sent: the
    /// server refuses envelopes issued more than five minutes earlier.
    /// </summary>
    public List<byte[]> BuildBatch(
        Guid winoUserId,
        Guid mailboxId,
        IReadOnlyList<MailIntelligenceUploadEnvelopeDto> messages,
        ContentEncryptionPublicKey transportKey)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(transportKey);

        var encryptor = new PemContentEnvelopeEncryptor(transportKey);
        var route = MailIntelligenceStreamRoutes.Request(mailboxId);
        var envelopes = new List<byte[]>(messages.Count);
        try
        {
            foreach (var message in messages)
            {
                envelopes.Add(Encrypt(encryptor, winoUserId, mailboxId, route, message));
            }
        }
        catch
        {
            Clear(envelopes);
            throw;
        }

        return envelopes;
    }

    public static void Clear(IEnumerable<byte[]> envelopes)
    {
        foreach (var envelope in envelopes)
        {
            CryptographicOperations.ZeroMemory(envelope);
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
