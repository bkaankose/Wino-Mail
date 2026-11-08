#nullable enable
using System.Net.WebSockets;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>
/// An open voice interpret socket, or why it could not be opened. <see cref="ErrorCode"/> is an API error code
/// (<c>AI_PACK_REQUIRED</c>, <c>AI_QUOTA_EXCEEDED</c>...) or a <see cref="WinoAccountClientErrorCodes"/> value.
/// </summary>
public sealed record VoiceConnectResult(WebSocket? Socket, string? ErrorCode)
{
    public static VoiceConnectResult Failed(string errorCode) => new(null, errorCode);
}
