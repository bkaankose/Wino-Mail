using Wino.Core.Domain;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Mail.WinUI.Services;

public static class WinoAccountAiErrorTranslator
{
    public static string Translate(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return Translator.GeneralTitle_Error;
        }

        return errorCode switch
        {
            ApiErrorCodes.AiPackRequired => Translator.WinoAccount_Error_AiPackRequired,
            ApiErrorCodes.AiQuotaExceeded => Translator.WinoAccount_Error_AiQuotaExceeded,
            ApiErrorCodes.AiHtmlEmpty => Translator.WinoAccount_Error_AiHtmlEmpty,
            ApiErrorCodes.AiHtmlTooLarge => Translator.WinoAccount_Error_AiHtmlTooLarge,
            ApiErrorCodes.AiUnsupportedLanguage => Translator.WinoAccount_Error_AiUnsupportedLanguage,
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
