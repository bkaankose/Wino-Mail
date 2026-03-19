using Wino.Core.Domain;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Mail.WinUI.Services;

public static class WinoAccountAuthErrorTranslator
{
    public static string Translate(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return Translator.GeneralTitle_Error;
        }

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
            _ => errorCode
        };
    }

    public static string Format(string? errorCode, string? errorMessage)
    {
        var translatedCode = Translate(errorCode);
        var hasCode = !string.IsNullOrWhiteSpace(errorCode);
        var hasMessage = !string.IsNullOrWhiteSpace(errorMessage);

        if (!hasCode && !hasMessage)
        {
            return Translator.GeneralTitle_Error;
        }

        var formattedCode = translatedCode;
        if (hasCode && !string.Equals(translatedCode, errorCode, System.StringComparison.Ordinal))
        {
            formattedCode = $"{translatedCode} ({errorCode})";
        }

        if (!hasMessage || string.Equals(errorMessage, translatedCode, System.StringComparison.OrdinalIgnoreCase) || string.Equals(errorMessage, errorCode, System.StringComparison.OrdinalIgnoreCase))
        {
            return formattedCode;
        }

        if (string.IsNullOrWhiteSpace(formattedCode))
        {
            return errorMessage!;
        }

        return $"{formattedCode}{System.Environment.NewLine}{errorMessage}";
    }
}
