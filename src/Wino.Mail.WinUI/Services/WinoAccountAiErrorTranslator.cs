using Wino.Core.Domain;

namespace Wino.Mail.WinUI.Services;

public static class WinoAccountAiErrorTranslator
{
    public static string Translate(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return Translator.GeneralTitle_Error;
        }

        return WinoAccountApiErrorTranslator.Translate(errorCode);
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

        if (IsTransportConsentRequired(errorCode, errorMessage))
        {
            return Translator.WinoAccount_TransportConsentRequired;
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

    public static bool IsTransportConsentRequired(string? errorCode, string? errorMessage)
        => errorCode is WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode or WinoAccountApiErrorTranslator.IntelligenceConsentVersionOutdatedCode ||
           errorMessage is WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode or WinoAccountApiErrorTranslator.IntelligenceConsentVersionOutdatedCode ||
           string.Equals(errorMessage, Translator.WinoAccount_TransportConsentRequired, System.StringComparison.Ordinal);
}
