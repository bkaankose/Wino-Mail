#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

/// <summary>How a streamed batch ended, as far as this device can tell.</summary>
public enum MailIntelligenceStreamEnd
{
    /// <summary>The done frame arrived and its count matched the result frames received.</summary>
    Done,

    /// <summary>The server stopped the batch with an error frame.</summary>
    Error,

    /// <summary>The response ended without a done or error frame: the connection dropped.</summary>
    Truncated,

    /// <summary>
    /// A frame was out of sequence, did not decrypt, was not an item, or the done count did not
    /// match. The rest of the stream is not read.
    /// </summary>
    Refused,
}

public sealed record MailIntelligenceStreamOutcome(
    MailIntelligenceStreamEnd End,
    int ResultFrames,
    string? ErrorCode = null,
    MailIntelligenceStreamSummaryDto? Summary = null);

/// <summary>
/// Reads the NDJSON response of one streamed batch. Every result frame must carry the next
/// sequence number and decrypt with the device result key under the frame route of this batch;
/// anything else ends the read. Items are handed over one at a time, as they arrive.
/// </summary>
public static class MailIntelligenceStreamReader
{
    /// <summary>Parses the response line by line without buffering the body.</summary>
    public static async IAsyncEnumerable<MailIntelligenceStreamFrameDto> ReadFramesAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw WinoAccountApiException.ServiceUnavailable(exception);
            }

            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MailIntelligenceStreamFrameDto? frame;
            try
            {
                frame = JsonSerializer.Deserialize(line, MailIntelligenceStreamJsonContext.Default.MailIntelligenceStreamFrameDto);
            }
            catch (JsonException exception)
            {
                throw WinoAccountApiException.InvalidResponse(HttpStatusCode.OK, exception);
            }

            if (frame is null || string.IsNullOrEmpty(frame.Type))
            {
                throw WinoAccountApiException.InvalidResponse(HttpStatusCode.OK);
            }

            yield return frame;
        }
    }

    /// <param name="decrypt">Opens one result envelope; receives the frame route it must be bound to.</param>
    /// <param name="onItem">Imports one item. Called in frame order, before the next frame is read.</param>
    /// <exception cref="IntelligenceResultKeyLostException">
    /// The first result frame of the batch did not decrypt: the device key is the problem, not the frame.
    /// </exception>
    public static async Task<MailIntelligenceStreamOutcome> ConsumeAsync(
        IAsyncEnumerable<MailIntelligenceStreamFrameDto> frames,
        Guid mailboxId,
        Guid batchId,
        Func<byte[], string, CancellationToken, Task<byte[]>> decrypt,
        Func<MailIntelligenceStreamItemDto, CancellationToken, Task> onItem,
        CancellationToken cancellationToken)
    {
        var expected = 0;
        var started = false;
        try
        {
            await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                started = true;
                switch (frame.Type)
                {
                    case MailIntelligenceStreamFrameTypes.Heartbeat:
                        continue;

                    case MailIntelligenceStreamFrameTypes.Done:
                        return frame.Sequence == expected
                            ? new(MailIntelligenceStreamEnd.Done, expected, Summary: frame.Summary)
                            : new(MailIntelligenceStreamEnd.Refused, expected, Summary: frame.Summary);

                    case MailIntelligenceStreamFrameTypes.Error:
                        return new(MailIntelligenceStreamEnd.Error, expected, frame.ErrorCode);

                    case MailIntelligenceStreamFrameTypes.Result:
                        break;

                    default:
                        // A frame type this build does not know carries nothing it could use.
                        continue;
                }

                if (frame.Sequence != expected || frame.Envelope is not { Length: > 0 } envelope)
                {
                    return new(MailIntelligenceStreamEnd.Refused, expected);
                }

                byte[] plaintext;
                try
                {
                    plaintext = await decrypt(
                        envelope, MailIntelligenceStreamRoutes.Frame(mailboxId, batchId, frame.Sequence), cancellationToken).ConfigureAwait(false);
                }
                catch (IntelligenceResultKeyLostException) when (expected > 0)
                {
                    // Earlier frames opened with the same key, so this frame is wrong, not the key.
                    return new(MailIntelligenceStreamEnd.Refused, expected);
                }

                MailIntelligenceStreamItemDto? item;
                try
                {
                    item = JsonSerializer.Deserialize(plaintext, MailIntelligenceStreamJsonContext.Default.MailIntelligenceStreamItemDto);
                }
                catch (JsonException)
                {
                    item = null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (item is null)
                {
                    return new(MailIntelligenceStreamEnd.Refused, expected);
                }

                expected++;
                await onItem(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WinoAccountApiException exception) when (exception.IsServiceUnavailable && started)
        {
            // The connection dropped mid-stream; what was imported stays. A failure before the first
            // frame is the caller's to report.
        }

        return new(MailIntelligenceStreamEnd.Truncated, expected);
    }
}

/// <summary>
/// The streamed route speaks the web defaults (camelCase) both ways. Item plaintext is the same
/// JSON the server wrote into v2 result pages.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MailIntelligenceStreamRequest))]
[JsonSerializable(typeof(MailIntelligenceStreamFrameDto))]
[JsonSerializable(typeof(MailIntelligenceStreamItemDto))]
internal sealed partial class MailIntelligenceStreamJsonContext : JsonSerializerContext;
