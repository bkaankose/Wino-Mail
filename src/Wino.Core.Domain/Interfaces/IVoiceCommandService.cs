#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Runs one assistant request against the voice API: one socket per request, audio or text in, transcript and plan out.
/// </summary>
public interface IVoiceCommandService
{
    /// <summary>
    /// Connects and sends the start frame with the device's time zone, clock and languages. A
    /// <see cref="VoiceInputKind.Text"/> request then sends its words with <see cref="IVoiceCommandSession.SendText"/>
    /// and never opens transcription. <paramref name="typedText"/> is what the user typed before switching to the
    /// microphone; the server plans it followed by the transcript. On failure
    /// <see cref="VoiceCommandStartResult.ErrorCode"/> says why and no session is returned.
    /// </summary>
    Task<VoiceCommandStartResult> StartAsync(VoiceInputKind input = VoiceInputKind.Audio, string? typedText = null, CancellationToken cancellationToken = default);
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

    /// <summary>Sends the words of a typed request. Only once, and only for a <see cref="VoiceInputKind.Text"/> request.</summary>
    void SendText(string text);
}

public sealed record VoiceCommandStartResult(IVoiceCommandSession? Session, string? ErrorCode);
