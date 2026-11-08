#nullable enable
using System;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>A microphone the platform can listen with. <see cref="Id"/> is the platform device id.</summary>
public sealed record MicrophoneDevice(string Id, string Name, bool IsDefault);

/// <summary>Why a microphone could or could not be opened.</summary>
public enum MicrophoneAccess
{
    Allowed,
    Denied,
    NoDevice,
    Unavailable
}

/// <summary>
/// The outcome of opening a microphone for a level monitor or a capture. <see cref="Session"/> is set only when access
/// is allowed; disposing it closes the microphone.
/// </summary>
public sealed record MicrophoneSessionResult(MicrophoneAccess Access, IDisposable? Session)
{
    public static MicrophoneSessionResult Failed(MicrophoneAccess access) => new(access, null);
}
