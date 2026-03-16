namespace Wino.Mail.Api.Contracts.Common;

public static class ApiErrorCodes
{
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string AccountBanned = "ACCOUNT_BANNED";
    public const string AccountSuspended = "ACCOUNT_SUSPENDED";
    public const string RefreshTokenInvalid = "REFRESH_TOKEN_INVALID";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string ExternalLoginEmailRequired = "EXTERNAL_LOGIN_EMAIL_REQUIRED";
    public const string ExternalLoginInvalid = "EXTERNAL_LOGIN_INVALID";
    public const string ExternalAuthStateInvalid = "EXTERNAL_AUTH_STATE_INVALID";
    public const string ExternalAuthCodeInvalid = "EXTERNAL_AUTH_CODE_INVALID";
    public const string AiPackRequired = "AI_PACK_REQUIRED";
    public const string AiQuotaExceeded = "AI_QUOTA_EXCEEDED";
    public const string AiHtmlEmpty = "AI_HTML_EMPTY";
    public const string AiHtmlTooLarge = "AI_HTML_TOO_LARGE";
    public const string AiUnsupportedLanguage = "AI_UNSUPPORTED_LANGUAGE";
    public const string AiSanitizationFailed = "AI_SANITIZATION_FAILED";
    public const string AiProviderUnavailable = "AI_PROVIDER_UNAVAILABLE";
    public const string AiRequestBlocked = "AI_REQUEST_BLOCKED";
    public const string AiInternalError = "AI_INTERNAL_ERROR";
    public const string PaddleWebhookInvalid = "PADDLE_WEBHOOK_INVALID";
    public const string Forbidden = "FORBIDDEN";
    public const string ValidationFailed = "VALIDATION_FAILED";
}
