using System.Text.Json;
using Wino.Core.Domain;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Mac copy of the Windows WinoAccountAuthErrorTranslator and WinoAccountEmailConfirmationHelper
/// (both live in the WinUI project): sign-in error codes to translated text, and the
/// email-confirmation details carried by a login failure.
/// </summary>
internal static class WinoAccountErrorText
{
    public static string Translate(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return Translator.GeneralTitle_Error;

        return errorCode switch
        {
            ApiErrorCodes.InvalidCredentials => Translator.WinoAccount_Error_InvalidCredentials,
            ApiErrorCodes.AccountLocked => Translator.WinoAccount_Error_AccountLocked,
            ApiErrorCodes.AccountBanned => Translator.WinoAccount_Error_AccountBanned,
            ApiErrorCodes.AccountSuspended => Translator.WinoAccount_Error_AccountSuspended,
            ApiErrorCodes.EmailNotConfirmed => Translator.WinoAccount_Error_EmailNotConfirmed,
            ApiErrorCodes.EmailConfirmationRequired => Translator.WinoAccount_Error_EmailConfirmationRequired,
            ApiErrorCodes.EmailConfirmationResendNotAvailable => Translator.WinoAccount_Error_EmailConfirmationResendNotAvailable,
            ApiErrorCodes.EmailConfirmationResendInvalid => Translator.WinoAccount_Error_EmailConfirmationResendInvalid,
            ApiErrorCodes.EmailNotRegistered => Translator.WinoAccount_Error_EmailNotRegistered,
            ApiErrorCodes.RefreshTokenInvalid => Translator.WinoAccount_Error_RefreshTokenInvalid,
            ApiErrorCodes.EmailAlreadyRegistered => Translator.WinoAccount_Error_EmailAlreadyRegistered,
            ApiErrorCodes.ExternalLoginEmailRequired => Translator.WinoAccount_Error_ExternalLoginEmailRequired,
            ApiErrorCodes.ExternalLoginInvalid => Translator.WinoAccount_Error_ExternalLoginInvalid,
            ApiErrorCodes.ExternalAuthStateInvalid => Translator.WinoAccount_Error_ExternalAuthStateInvalid,
            ApiErrorCodes.ExternalAuthCodeInvalid => Translator.WinoAccount_Error_ExternalAuthCodeInvalid,
            ApiErrorCodes.Forbidden => Translator.WinoAccount_Error_Forbidden,
            ApiErrorCodes.ValidationFailed => Translator.WinoAccount_Error_ValidationFailed,
            _ => WinoAccountApiErrorTranslator.Translate(errorCode)
        };
    }

    /// <summary>Translated code, with the code and the server message appended when they add information.</summary>
    public static string Format(string? errorCode, string? errorMessage)
    {
        if (WinoAccountClientErrorCodes.IsServiceFailure(errorCode) || errorCode == WinoAccountClientErrorCodes.SignInRequired)
            return Translate(errorCode);

        var translated = Translate(errorCode);
        var hasCode = !string.IsNullOrWhiteSpace(errorCode);
        var hasMessage = !string.IsNullOrWhiteSpace(errorMessage);
        if (!hasCode && !hasMessage) return Translator.GeneralTitle_Error;

        var formatted = hasCode && !string.Equals(translated, errorCode, StringComparison.Ordinal) ? $"{translated} ({errorCode})" : translated;
        if (!hasMessage || string.Equals(errorMessage, translated, StringComparison.OrdinalIgnoreCase) || string.Equals(errorMessage, errorCode, StringComparison.OrdinalIgnoreCase))
            return formatted;
        return string.IsNullOrWhiteSpace(formatted) ? errorMessage! : $"{formatted}{Environment.NewLine}{errorMessage}";
    }

    public static bool IsEmailConfirmationRequired(string? errorCode)
        => string.Equals(errorCode, ApiErrorCodes.EmailNotConfirmed, StringComparison.Ordinal)
           || string.Equals(errorCode, ApiErrorCodes.EmailConfirmationRequired, StringComparison.Ordinal);

    public static EmailConfirmationRequiredDetailsDto? ParseConfirmation(JsonElement? details)
    {
        if (details is not JsonElement element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try
        {
            if (!TryString(element, "resendConfirmationEndpoint", out var endpoint)
                || !TryString(element, "resendConfirmationTicket", out var ticket)
                || !TryString(element, "resendAvailableAtUtc", out var availableText)
                || !DateTimeOffset.TryParse(availableText, out var available))
                return null;

            DateTimeOffset? latest = TryString(element, "latestConfirmationEmailSentUtc", out var latestText) && DateTimeOffset.TryParse(latestText, out var parsed)
                ? parsed : null;
            return new EmailConfirmationRequiredDetailsDto(endpoint, ticket, latest, available);
        }
        catch (FormatException) { return null; }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
