using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain;

/// <summary>
/// Converts stable Wino Account API error codes into user-facing text.
/// Unknown codes are preserved so newly introduced server failures remain diagnosable.
/// </summary>
public static class WinoAccountApiErrorTranslator
{
    public const string IntelligenceConsentRequiredCode = "INTELLIGENCE_CONSENT_REQUIRED";
    public const string IntelligenceConsentVersionOutdatedCode = "INTELLIGENCE_CONSENT_VERSION_OUTDATED";
    public const string IntelligenceDeletionPendingCode = "INTELLIGENCE_DELETION_PENDING";
    public const string IntelligenceDeletionFailedCode = "INTELLIGENCE_DELETION_FAILED";

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
            ApiErrorCodes.RefreshTokenInvalid => Translator.WinoAccount_Error_RefreshTokenInvalid,
            ApiErrorCodes.EmailAlreadyRegistered => Translator.WinoAccount_Error_EmailAlreadyRegistered,
            ApiErrorCodes.EmailNotRegistered => Translator.WinoAccount_Error_EmailNotRegistered,
            ApiErrorCodes.EmailNotConfirmed => Translator.WinoAccount_Error_EmailNotConfirmed,
            ApiErrorCodes.EmailConfirmationRequired => Translator.WinoAccount_Error_EmailConfirmationRequired,
            ApiErrorCodes.EmailConfirmationResendNotAvailable => Translator.WinoAccount_Error_EmailConfirmationResendNotAvailable,
            ApiErrorCodes.EmailConfirmationResendInvalid => Translator.WinoAccount_Error_EmailConfirmationResendInvalid,
            ApiErrorCodes.ExternalLoginEmailRequired => Translator.WinoAccount_Error_ExternalLoginEmailRequired,
            ApiErrorCodes.ExternalLoginInvalid => Translator.WinoAccount_Error_ExternalLoginInvalid,
            ApiErrorCodes.ExternalAuthStateInvalid => Translator.WinoAccount_Error_ExternalAuthStateInvalid,
            ApiErrorCodes.ExternalAuthCodeInvalid => Translator.WinoAccount_Error_ExternalAuthCodeInvalid,
            ApiErrorCodes.AiPackRequired => Translator.WinoAccount_Error_AiPackRequired,
            ApiErrorCodes.AiQuotaExceeded => Translator.WinoAccount_Error_AiQuotaExceeded,
            ApiErrorCodes.AiHtmlEmpty => Translator.WinoAccount_Error_AiHtmlEmpty,
            ApiErrorCodes.AiHtmlTooLarge => Translator.WinoAccount_Error_AiHtmlTooLarge,
            ApiErrorCodes.AiUnsupportedLanguage => Translator.WinoAccount_Error_AiUnsupportedLanguage,
            ApiErrorCodes.AiSanitizationFailed => Translator.WinoAccount_Error_AiSanitizationFailed,
            ApiErrorCodes.AiProviderUnavailable => Translator.WinoAccount_Error_AiProviderUnavailable,
            ApiErrorCodes.AiRequestBlocked => Translator.WinoAccount_Error_AiRequestBlocked,
            ApiErrorCodes.AiInternalError => Translator.WinoAccount_Error_AiInternalError,
            ApiErrorCodes.BillingProductInvalid => Translator.WinoAccount_Error_BillingProductInvalid,
            ApiErrorCodes.BillingProductAlreadyOwned => Translator.WinoAccount_Error_BillingProductAlreadyOwned,
            ApiErrorCodes.BillingSubscriptionAlreadyExists => Translator.WinoAccount_Error_BillingSubscriptionAlreadyExists,
            ApiErrorCodes.BillingCustomerNotFound => Translator.WinoAccount_Error_BillingCustomerNotFound,
            ApiErrorCodes.BillingUnavailable or ApiErrorCodes.StripeWebhookInvalid => Translator.WinoAccount_Error_BillingUnavailable,
            ApiErrorCodes.SemanticIndexNotConfigured => Translator.WinoAccount_Error_SemanticIndexNotConfigured,
            ApiErrorCodes.SemanticMailboxNotFound => Translator.SemanticIndex_MailboxUnavailable,
            ApiErrorCodes.SemanticIndexNotFound => Translator.WinoAccount_Management_NoIntelligenceData,
            ApiErrorCodes.SemanticIndexProfileUnsupported => Translator.WinoAccount_Error_SemanticIndexProfileUnsupported,
            ApiErrorCodes.SemanticIndexHashMismatch => Translator.WinoAccount_Error_SemanticIndexHashMismatch,
            ApiErrorCodes.SemanticMailboxLimitExceeded => Translator.SemanticIndex_MailboxLimitExceeded,
            ApiErrorCodes.SemanticIndexStorageLimitExceeded or ApiErrorCodes.IntelligenceStorageLimitExceeded => Translator.SemanticIndex_StorageLimitExceeded,
            ApiErrorCodes.IntelligenceEnvelopeInvalid => Translator.WinoAccount_Error_IntelligenceEnvelopeInvalid,
            ApiErrorCodes.IntelligenceProfileInvalid => Translator.WinoAccount_Error_IntelligenceProfileInvalid,
            ApiErrorCodes.IntelligenceRequestConflict => Translator.WinoAccount_Error_IntelligenceRequestConflict,
            IntelligenceConsentRequiredCode or IntelligenceConsentVersionOutdatedCode => Translator.WinoAccount_TransportConsentRequired,
            IntelligenceDeletionPendingCode => Translator.WinoAccount_IntelligenceDeletionPending,
            IntelligenceDeletionFailedCode => Translator.WinoAccount_IntelligenceDeletionFailed,
            ApiErrorCodes.EmailConfigurationInvalid => Translator.WinoAccount_Error_EmailConfigurationInvalid,
            ApiErrorCodes.InternalServerError => Translator.WinoAccount_Error_InternalServerError,
            ApiErrorCodes.Forbidden => Translator.WinoAccount_Error_Forbidden,
            ApiErrorCodes.ValidationFailed => Translator.WinoAccount_Error_ValidationFailed,
            _ => errorCode,
        };
    }
}
