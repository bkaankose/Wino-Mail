namespace Wino.Core.Domain.Models.Voice;

/// <summary>Voice failures the app detects itself, next to the API's error codes and the socket's error frames.</summary>
public static class VoiceClientErrorCodes
{
    /// <summary>The API refused the request with 403 and did not say why (AI Pack, consent or quota).</summary>
    public const string AccessDenied = "WINO_VOICE_ACCESS_DENIED";

    /// <summary>The socket closed or failed before the API sent a plan or an error.</summary>
    public const string ConnectionLost = "WINO_VOICE_CONNECTION_LOST";

    /// <summary>The API sent a frame the app could not read.</summary>
    public const string InvalidFrame = "WINO_VOICE_INVALID_FRAME";
}
