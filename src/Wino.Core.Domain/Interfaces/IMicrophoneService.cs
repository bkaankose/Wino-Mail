#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Lists the microphones and reads their input level for voice control. Implementations never throw for
/// missing devices, denied access or audio failures; they report them through <see cref="MicrophoneAccess"/>.
/// </summary>
public interface IMicrophoneService
{
    /// <summary>Raised on any thread when a microphone is added, removed or the system default changes.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>The enabled microphones, or an empty list when none can be listed.</summary>
    Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user or the system has blocked microphone access. Never asks the user and never opens a
    /// microphone, so it is safe to call when a page only shows settings.
    /// </summary>
    MicrophoneAccess GetAccessStatus();

    /// <summary>
    /// Starts reading the input level of <paramref name="deviceId"/>, or of the system default microphone when it is
    /// empty or no longer present. This opens the microphone, so the platform may ask the user for access: call it
    /// only after the user asked for input, such as a microphone test. <paramref name="levelChanged"/> receives a level from 0 to 1 on an audio thread.
    /// Dispose the returned monitor to stop listening.
    /// </summary>
    Task<MicrophoneSessionResult> StartLevelMonitorAsync(string? deviceId, Action<double> levelChanged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="deviceId"/> (or the system default) as 16-bit little-endian mono PCM at
    /// <paramref name="sampleRate"/>. <paramref name="audioAvailable"/> receives chunks of about 100 ms and
    /// <paramref name="levelChanged"/> a level from 0 to 1, both on an audio thread. Disposing the session delivers the
    /// last partial chunk before it closes the microphone. Like <see cref="StartLevelMonitorAsync"/>, it may ask the
    /// user for access, so call it only when the user starts speaking.
    /// </summary>
    Task<MicrophoneSessionResult> StartCaptureAsync(
        string? deviceId,
        int sampleRate,
        Action<ReadOnlyMemory<byte>> audioAvailable,
        Action<double> levelChanged,
        CancellationToken cancellationToken = default);
}
