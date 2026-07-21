using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.AppServices.Contracts;

public static class AppServiceProtocol
{
    public const string ServiceName = "Wino.Companion.AppService";
    public const string MessageKindKey = "kind";
    public const string PayloadKey = "payload";
    public const string SuccessKey = "success";
    public const string ErrorCodeKey = "errorCode";
    public const string ErrorMessageKey = "errorMessage";

    public const string InvokeKind = "invoke";
    public const string PublishKind = "publish";
    public const string ClientPublishKind = "client-publish";
    public const string DetachKind = "detach";
    public const string ReloadPreferencesKind = "reload-preferences";
}

public enum RpcErrorCode
{
    None = 0,
    InvalidRequest,
    UnsupportedProtocol,
    UnsupportedMethod,
    UnauthorizedClient,
    OperationCanceled,
    InteractiveWindowUnavailable,
    InteractiveWindowInvalid,
    AuthenticationRequired,
    CompanionUnavailable,
    InternalError,
}

public enum RequiredUserAction
{
    None = 0,
    OpenApplication,
    Reauthenticate,
    ReviewAccount,
    Retry,
}

public enum ClientDetachReason
{
    WindowClosed = 0,
    Suspended,
    Terminating,
}

public sealed record RpcRequest(
    int UwpProcessId,
    long WindowHandle,
    string MethodId,
    string? Payload);

public sealed record RpcResponse(
    bool IsSuccess,
    string? Payload,
    RpcErrorCode ErrorCode,
    RequiredUserAction RequiredUserAction,
    string? ErrorMessage = null)
{
    public static RpcResponse Success(string? payload = null) =>
        new(true, payload, RpcErrorCode.None, RequiredUserAction.None);

    public static RpcResponse Failure(
        RpcErrorCode code,
        string? message,
        RequiredUserAction userAction = RequiredUserAction.None) =>
        new(false, null, code, userAction, message);
}

public sealed record MessengerEnvelope(string MessageId, string PayloadJson);

public sealed record ClientMessengerEnvelope(MessengerEnvelope Message);

public sealed record ClientDetachRequest(ClientDetachReason Reason);

public sealed record PreferenceReloadRequest(long Revision);

public sealed record BackgroundToastActionRequest(
    string Arguments,
    IReadOnlyDictionary<string, string> UserInput);

public sealed record MailNotificationsRequest(IReadOnlyList<Guid> MailIds);

public sealed record AccountAttentionNotificationRequest(Guid AccountId);

public sealed record CalendarReminderNotificationRequest(Guid CalendarItemId, long ReminderDurationInSeconds);

public sealed record BadgeCountRequest(int Count);

public sealed record RemoveMailNotificationRequest(Guid MailId);

public sealed record ShutdownFlushRequest(Guid RequestId, long DeadlineUtcTicks, bool ExitUi);

public sealed record ShutdownFlushCompleted(Guid RequestId);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(MessengerEnvelope))]
[JsonSerializable(typeof(ClientMessengerEnvelope))]
[JsonSerializable(typeof(ClientDetachRequest))]
[JsonSerializable(typeof(PreferenceReloadRequest))]
[JsonSerializable(typeof(BackgroundToastActionRequest))]
[JsonSerializable(typeof(MailNotificationsRequest))]
[JsonSerializable(typeof(AccountAttentionNotificationRequest))]
[JsonSerializable(typeof(CalendarReminderNotificationRequest))]
[JsonSerializable(typeof(BadgeCountRequest))]
[JsonSerializable(typeof(RemoveMailNotificationRequest))]
[JsonSerializable(typeof(ShutdownFlushRequest))]
[JsonSerializable(typeof(ShutdownFlushCompleted))]
public partial class WinoAppServiceJsonContext : JsonSerializerContext
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Default.GetTypeInfo(typeof(T))!);

    public static T Deserialize<T>(string json) =>
        (T?)JsonSerializer.Deserialize(json, Default.GetTypeInfo(typeof(T))!)
        ?? throw new JsonException($"The {typeof(T).Name} AppService payload was null.");
}
