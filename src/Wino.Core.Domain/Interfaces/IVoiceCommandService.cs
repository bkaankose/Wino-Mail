#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Runs one spoken command against the voice API: one socket per command, audio in, transcript and plan out.
/// </summary>
public interface IVoiceCommandService
{
    /// <summary>
    /// Connects and sends the start frame with the device's time zone, clock and languages. On failure
    /// <see cref="VoiceCommandStartResult.ErrorCode"/> says why and no session is returned.
    /// </summary>
    Task<VoiceCommandStartResult> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One command in flight. Audio sent before the API is ready is held and sent once it is. <see cref="Frames"/>
/// completes after the plan, an error, or a lost connection; a lost connection arrives as an error frame with
/// <see cref="Models.Voice.VoiceClientErrorCodes.ConnectionLost"/>. Disposing cancels the command.
/// </summary>
public interface IVoiceCommandSession : IAsyncDisposable
{
    ChannelReader<VoiceServerFrame> Frames { get; }

    /// <summary>Queues 16-bit mono PCM at <see cref="VoiceAudioFormat.SampleRate"/>. Safe to call from an audio thread.</summary>
    void SendAudio(ReadOnlyMemory<byte> pcm);

    /// <summary>Tells the API the user stopped speaking. Later audio is dropped.</summary>
    void Commit();
}

public sealed record VoiceCommandStartResult(IVoiceCommandSession? Session, string? ErrorCode);
