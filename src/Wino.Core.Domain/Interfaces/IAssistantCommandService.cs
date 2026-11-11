#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Runs one assistant request against the interpret socket (<c>GET /api/v2/assistant/interpret</c>): one socket per
/// request, audio or typed text in, transcript frames and the reading out. The capture transport only; the reading
/// is handed to the Wino Agent Runtime, which resolves it on the device.
/// </summary>
public interface IAssistantCommandService
{
    /// <summary>
    /// Connects and sends the start frame with <paramref name="request"/> (its <c>Text</c> is ignored: a typed request
    /// then sends its words with <see cref="IAssistantCommandSession.SendText"/>, a spoken one streams audio and
    /// commits). On failure <see cref="AssistantCommandStartResult.ErrorCode"/> says why and no session is returned.
    /// </summary>
    Task<AssistantCommandStartResult> StartAsync(AssistantReadRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// One request in flight. Audio sent before the API is ready is held and sent once it is. <see cref="Frames"/>
/// completes after the reading, an error, or a lost connection; a lost connection arrives as an error frame with
/// <see cref="Models.Voice.VoiceClientErrorCodes.ConnectionLost"/>. Disposing cancels the request.
/// </summary>
public interface IAssistantCommandSession : IAsyncDisposable
{
    ChannelReader<AssistantServerFrame> Frames { get; }

    /// <summary>Queues 16-bit mono PCM at <see cref="VoiceAudioFormat.SampleRate"/>. Safe to call from an audio thread.</summary>
    void SendAudio(ReadOnlyMemory<byte> pcm);

    /// <summary>Tells the API the user stopped speaking. Later audio is dropped.</summary>
    void Commit();

    /// <summary>Sends the words of a typed request. Only once, and only for a typed request.</summary>
    void SendText(string text);
}

public sealed record AssistantCommandStartResult(IAssistantCommandSession? Session, string? ErrorCode);
