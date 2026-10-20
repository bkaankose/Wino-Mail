#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Store;
using Wino.Mail.Api.Contracts.Users;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAccountApiClient
{
    Task<WinoAccountApiResult<AuthResultDto>> RegisterWithProfileAsync(string email, string password, string? displayName, CancellationToken cancellationToken = default)
        => RegisterAsync(email, password, cancellationToken);
    Task<ApiEnvelope<AuthUserDto>> UpdateProfileAsync(string? displayName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<ApiEnvelope<AuthUserDto>> UploadAvatarAsync(byte[] payload, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<ApiEnvelope<AuthUserDto>> DeleteAvatarAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<(byte[] Payload, Guid Revision)?> GetAvatarAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<WinoAccountApiResult<AuthResultDto>> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountApiResult<AuthResultDto>> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    /// <summary>Native Sign in with Apple: the identity token and authorization code from AuthenticationServices; the name only on first authorization.</summary>
    Task<WinoAccountApiResult<AuthResultDto>> SignInWithAppleNativeAsync(string identityToken, string authorizationCode, string? givenName, string? familyName, CancellationToken cancellationToken = default);
    /// <summary>Native Google sign-in: an ID token issued to one of the Wino Accounts Google clients.</summary>
    Task<WinoAccountApiResult<AuthResultDto>> SignInWithGoogleNativeAsync(string idToken, CancellationToken cancellationToken = default);
    /// <summary>Finishes the browser flow with the completion code the API redirected to <see cref="Models.Accounts.WinoAccountExternalAuth.CallbackUri"/>.</summary>
    Task<WinoAccountApiResult<AuthResultDto>> CompleteExternalSignInAsync(string code, CancellationToken cancellationToken = default);
    /// <summary>The absolute address that starts the browser flow for <paramref name="provider"/> (see <see cref="Models.Accounts.WinoAccountExternalAuth.StartUri"/>).</summary>
    Uri GetExternalSignInAuthorizationUri(string provider);
    Task<WinoAccountApiResult<AuthResultDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Records that the signed-in Wino Account accepted app privacy policy <paramref name="version"/>
    /// (<c>POST api/v1/users/me/app-policy/accept</c>). Idempotent; <paramref name="source"/> is an
    /// <see cref="AppPolicyAcceptanceSources"/> value and <paramref name="platform"/> a relay platform value.
    /// </summary>
    Task<ApiEnvelope<AppPolicyAcceptanceDto>> AcceptAppPolicyAsync(int version, string source, string platform, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
    /// <param name="context">A <see cref="RewriteContexts"/> value: reading a received message or composing a draft.</param>
    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string language, string context, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(string productCode, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<BillingStatusResultDto>> GetBillingStatusAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<StoreCollectionsIdTicketResultDto>> CreateStoreCollectionsIdTicketAsync(CancellationToken cancellationToken = default);
    /// <summary>Moves a Microsoft Store Unlimited Accounts purchase onto the signed-in Wino Account.</summary>
    Task<ApiEnvelope<StorePurchaseRedeemResultDto>> RedeemStoreUnlimitedAccountsAsync(string storeIdKey, CancellationToken cancellationToken = default);
    /// <summary>
    /// Links App Store purchases to the signed-in Wino Account. Each item is a StoreKit 2 signed transaction
    /// (1 to 20). The server verifies them with Apple; a purchase already linked elsewhere stays there.
    /// </summary>
    Task<ApiEnvelope<AppStoreRedeemResultDto>> RedeemAppStoreTransactionsAsync(IReadOnlyList<string> signedTransactions, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiUsageStatusDto>> GetAiUsageAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Exchanges the Wino access token for a short-lived WinoRelay token (<c>POST /api/v1/relay/token</c>).
    /// A 503 answers with <see cref="Models.PushRelay.RelayApiErrorCodes.RelayUnavailable"/>.
    /// </summary>
    Task<ApiEnvelope<Models.PushRelay.RelayTokenDto>> CreateRelayTokenAsync(CancellationToken cancellationToken = default);
    /// <summary>Anonymous: a short-lived nonce for device registration (<c>POST api/v1/relay/devices/challenge</c>).</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayDeviceChallengeDto>> CreateRelayDeviceChallengeAsync(string platform, CancellationToken cancellationToken = default);
    /// <summary>Anonymous: registers this install as a relay device and returns its credential (<c>POST api/v1/relay/devices</c>).</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayDeviceDto>> RegisterRelayDeviceAsync(Models.PushRelay.RegisterRelayDeviceRequest request, CancellationToken cancellationToken = default);
    /// <summary>Device credential instead of a Wino Account: a relay token for the device subject.</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayTokenDto>> CreateRelayTokenForDeviceAsync(Guid deviceId, string credential, Models.PushRelay.RelayDeviceAssertion? assertion, CancellationToken cancellationToken = default);
    /// <summary>Signed in: links the device (proven by its credential) to the signed-in Wino Account.</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayDeviceDto>> LinkRelayDeviceAsync(Guid deviceId, string credential, Models.PushRelay.RelayDeviceAssertion? assertion, CancellationToken cancellationToken = default);
    /// <summary>Device credential: removes the Wino Account from the device; the device keeps working anonymously.</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayDeviceDto>> UnlinkRelayDeviceAsync(Guid deviceId, string credential, Models.PushRelay.RelayDeviceAssertion? assertion, CancellationToken cancellationToken = default);
    /// <summary>Signed in: the devices linked to the Wino Account (<c>GET api/v1/relay/devices</c>).</summary>
    Task<ApiEnvelope<Models.PushRelay.RelayDeviceListDto>> GetRelayDevicesAsync(CancellationToken cancellationToken = default);
    /// <summary>The stored sync snapshot bytes and revision, or null when the account has none.</summary>
    Task<WinoSyncSnapshotDownload?> GetSyncSnapshotAsync(CancellationToken cancellationToken = default);
    /// <summary>Metadata for the stored sync snapshot without downloading its encrypted payload.</summary>
    Task<UserSyncSnapshotStatusDto?> GetSyncSnapshotStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Uploads the encrypted snapshot. A non-null expected revision makes the write conditional.</summary>
    Task<UserSyncSnapshotStatusDto> PutSyncSnapshotAsync(byte[] payload, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default);
    Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default);
    // Mail intelligence. Jobs are submitted per mailbox; results are collected as two
    // independently downloadable and independently acknowledged stages.
    Task<MailIntelligenceJobAcceptedDto> SubmitMailIntelligenceJobAsync(
        Guid mailboxId, Guid jobId, string checksum, byte[] upload, CancellationToken cancellationToken = default);
    Task<MailIntelligenceJobListDto> GetMailIntelligenceJobsAsync(string? resultKeyId = null, CancellationToken cancellationToken = default);
    Task<IntelligenceTransportKeyDto> GetIntelligenceTransportKeyAsync(CancellationToken cancellationToken = default);
    /// <param name="waitSeconds">
    /// When above zero, the server holds the request until a stage can be downloaded or the job
    /// ends (or the wait runs out), instead of answering at once.
    /// </param>
    Task<MailIntelligenceJobDto?> GetMailIntelligenceJobAsync(Guid mailboxId, Guid jobId, int waitSeconds = 0, CancellationToken cancellationToken = default);
    Task<byte[]> GetMailIntelligenceResultPageAsync(Guid mailboxId, Guid jobId, string stage, int page, CancellationToken cancellationToken = default);
    Task<MailIntelligenceStageAckResultDto> AcknowledgeMailIntelligenceStageAsync(
        Guid mailboxId, Guid jobId, string stage, string digest, CancellationToken cancellationToken = default);
    Task CancelMailIntelligenceJobAsync(Guid mailboxId, Guid jobId, CancellationToken cancellationToken = default);
    Task<AnalyzeMailResponseDto> AnalyzeMailAsync(
        Guid mailboxId, byte[] encryptedEnvelope, string language, CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> GetIntelligenceConsentAsync(CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> AcceptIntelligenceConsentAsync(string policyVersion, string source, CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> RevokeIntelligenceConsentAsync(string source, CancellationToken cancellationToken = default);
}
