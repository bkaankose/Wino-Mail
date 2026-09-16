using System.Net;

namespace Wino.Mapi;

/// <summary>Base for everything this library throws on purpose.</summary>
public class MapiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A buffer did not parse. The message says where; the bytes are the caller's to dump.</summary>
public sealed class MapiFormatException(string message) : MapiException(message);

/// <summary>
/// Autodiscover answered for the mailbox but listed no mapiHttp protocol: the server predates
/// Exchange 2013 SP1, or MAPI/HTTP is disabled for the organisation or the user. EWS is the fallback.
/// </summary>
public sealed class MapiNotAdvertisedException(string message) : MapiException(message);

/// <summary>
/// The HTTP transport did not accept a request: a non-2xx status, or a non-zero X-ResponseCode
/// (MS-OXCMAPIHTTP 2.2.3.3.3). ROP-level failures are not this; they arrive inside a successful
/// transport response.
/// </summary>
public sealed class MapiTransportException(string requestType, HttpStatusCode httpStatus, string? responseCode, string message)
    : MapiException(message)
{
    public string RequestType { get; } = requestType;
    public HttpStatusCode HttpStatus { get; } = httpStatus;
    public string? ResponseCode { get; } = responseCode;

    /// <summary>X-ResponseCode 10, ContextNotFound: the session is gone, or a Connect never succeeded.</summary>
    public bool IsContextNotFound => ResponseCode == "10";

    public bool IsUnauthorized => HttpStatus == HttpStatusCode.Unauthorized;
}

/// <summary>
/// The Connect request was accepted by the transport but refused by the server (a non-zero ErrorCode
/// in the Connect response, MS-OXCMAPIHTTP 2.2.4.1.2). Without a session every later request fails
/// as ContextNotFound, which looks like a cookie bug and is not one; hence a distinct exception.
/// </summary>
public sealed class MapiConnectException(uint errorCode)
    : MapiException($"Connect refused, ErrorCode 0x{errorCode:X8} ({Describe(errorCode)}).")
{
    public uint ErrorCode { get; } = errorCode;

    public static string Describe(uint code) => code switch
    {
        0x000003F2 or 0x000003EB => "the authenticated identity may not open the mailbox named by the UserDn / MailboxId",
        _ => "unknown",
    };
}

/// <summary>
/// The Execute request was refused at the RPC layer (a non-zero ErrorCode in the Execute response,
/// MS-OXCRPC 2.2.1). What follows in such a response is a diagnostic, not ROPs.
/// </summary>
public sealed class MapiRpcException(uint errorCode)
    : MapiException($"Execute returned ErrorCode 0x{errorCode:X8} ({Describe(errorCode)}).")
{
    public uint ErrorCode { get; } = errorCode;

    public const uint BufferTooSmall = 0x0000047D;
    public const uint RpcFormat = 0x000004B6;
    public const uint LoginFailure = 0x000003F2;

    public static string Describe(uint code) => code switch
    {
        BufferTooSmall => "ecBufferTooSmall: the ROP response would not fit the 32KB ROP buffer; ask for less per ROP",
        RpcFormat => "ecRpcFormat: malformed request buffer",
        LoginFailure => "ecLoginFailure: not authorized for this mailbox",
        0x00000005 => "ecAccessDenied",
        _ => "unknown",
    };
}

/// <summary>A ROP ran and reported failure through its ReturnValue (MS-OXCDATA 2.4).</summary>
public sealed class MapiRopException(string ropName, uint returnValue, string? trailingResponse = null)
    : MapiException($"{ropName} failed, ReturnValue 0x{returnValue:X8} ({Describe(returnValue)})." + (trailingResponse is null ? string.Empty : " Trailing response bytes:\n" + trailingResponse))
{
    public string RopName { get; } = ropName;
    public uint ReturnValue { get; } = returnValue;

    /// <summary>Whatever followed the return value in the failed ROP's response, as a hex dump; null when nothing did.</summary>
    public string? TrailingResponse { get; } = trailingResponse;

    public const uint NotFound = 0x8004010F;
    public const uint AccessDenied = 0x80070005;
    public const uint NotEnoughMemory = 0x8007000E;
    public const uint NotSupported = 0x80040102;

    public static string Describe(uint code) => code switch
    {
        NotFound => "MAPI_E_NOT_FOUND",
        AccessDenied => "MAPI_E_NO_ACCESS",
        NotEnoughMemory => "MAPI_E_NOT_ENOUGH_MEMORY (a property too large to return inline; stream it)",
        NotSupported => "MAPI_E_NO_SUPPORT",
        0x80040111 => "MAPI_E_LOGON_FAILED",
        0x80040115 => "MAPI_E_NETWORK_ERROR",
        0x80040117 => "MAPI_E_TOO_COMPLEX",
        0x8004011B => "MAPI_E_CORRUPT_DATA",
        0x80040109 => "MAPI_E_OBJECT_CHANGED",
        0x8004010A => "MAPI_E_OBJECT_DELETED",
        _ => "unknown",
    };
}

/// <summary>
/// A content read on a ghosted folder failed: the folder's contents live in another public folder
/// mailbox, named by the replica list RopOpenFolder returned. The caller opens a session there and
/// reads again.
/// </summary>
public sealed class MapiGhostedFolderException(IReadOnlyList<string> replicas, MapiRopException inner)
    : MapiException($"The folder's contents are held elsewhere ({string.Join(", ", replicas)}); {inner.Message}", inner)
{
    public IReadOnlyList<string> Replicas { get; } = replicas;
}
