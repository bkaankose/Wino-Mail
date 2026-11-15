#if DEBUG
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Services;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug-only stand-in for the Wino Account profile service. While <c>winoacct-simulate refresh</c> is on, every
/// profile or token refresh answers "sign-in required" without calling the API or writing the stored account,
/// as the service does when the session needs a new sign-in. The stored account stays signed in; turning the
/// simulation off restores the real answers.
/// </summary>
internal sealed class WinoAccountSessionSimulation(WinoAccountProfileService inner) : IWinoAccountProfileService
{
    private static bool _refreshNeedsSignIn;

    private IWinoAccountProfileService Inner => inner;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<WinoAccountProfileService>();
        services.Replace(ServiceDescriptor.Singleton<IWinoAccountProfileService>(provider =>
            new WinoAccountSessionSimulation(provider.GetRequiredService<WinoAccountProfileService>())));
        MacDebugBridge.Register("winoacct-simulate", args =>
        {
            if (args.Length > 0) _refreshNeedsSignIn = args[0].Equals("refresh", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(_refreshNeedsSignIn ? "refresh needs sign-in" : "off");
        });
    }

    private static WinoAccountOperationResult SignInRequired => WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.SignInRequired);

    public Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
        => _refreshNeedsSignIn ? Task.FromResult(SignInRequired) : Inner.RefreshAsync(cancellationToken);

    public Task<WinoAccountOperationResult> RefreshProfileAsync(CancellationToken cancellationToken = default)
        => _refreshNeedsSignIn ? Task.FromResult(SignInRequired) : Inner.RefreshProfileAsync(cancellationToken);

    public Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default)
        => _refreshNeedsSignIn ? Task.FromResult<WinoAccount?>(null) : Inner.GetAuthenticatedAccountAsync(cancellationToken);

    public Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => _refreshNeedsSignIn ? Task.FromResult(ApiEnvelope<AuthUserDto>.Failure(WinoAccountClientErrorCodes.SignInRequired)) : Inner.GetCurrentUserAsync(cancellationToken);

    public Task<WinoAccountOperationResult> RegisterWithProfileAsync(string email, string password, string? displayName, CancellationToken cancellationToken = default)
        => Inner.RegisterWithProfileAsync(email, password, displayName, cancellationToken);
    public Task<ApiEnvelope<AuthUserDto>> UpdateProfileAsync(string? displayName, CancellationToken cancellationToken = default) => Inner.UpdateProfileAsync(displayName, cancellationToken);
    public Task<ApiEnvelope<AuthUserDto>> UploadAvatarAsync(byte[] payload, CancellationToken cancellationToken = default) => Inner.UploadAvatarAsync(payload, cancellationToken);
    public Task<ApiEnvelope<AuthUserDto>> DeleteAvatarAsync(CancellationToken cancellationToken = default) => Inner.DeleteAvatarAsync(cancellationToken);
    public Task<byte[]> PrepareAvatarAsync(byte[] payload, CancellationToken cancellationToken = default) => Inner.PrepareAvatarAsync(payload, cancellationToken);
    public Task<string?> GetAvatarPathAsync(Guid accountId, Guid? revision, CancellationToken cancellationToken = default) => Inner.GetAvatarPathAsync(accountId, revision, cancellationToken);
    public Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) => Inner.RegisterAsync(email, password, cancellationToken);
    public Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) => Inner.LoginAsync(email, password, cancellationToken);
    public Task<WinoAccountOperationResult> SignInWithAppleAsync(AppleSignInCredential credential, CancellationToken cancellationToken = default) => Inner.SignInWithAppleAsync(credential, cancellationToken);
    public Task<WinoAccountOperationResult> SignInWithGoogleAsync(string idToken, CancellationToken cancellationToken = default) => Inner.SignInWithGoogleAsync(idToken, cancellationToken);
    public Task<WinoAccountOperationResult> CompleteExternalSignInAsync(string code, CancellationToken cancellationToken = default) => Inner.CompleteExternalSignInAsync(code, cancellationToken);
    public Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default)
        => Inner.ResendEmailConfirmationAsync(endpoint, ticket, cancellationToken);
    public Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => Inner.ForgotPasswordAsync(email, cancellationToken);
    public Task<WinoAccount?> GetActiveAccountAsync() => Inner.GetActiveAccountAsync();
    public Task<bool> HasActiveAccountAsync() => Inner.HasActiveAccountAsync();
    public Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default)
        => Inner.SummarizeAsync(segments, targetLanguage, cancellationToken);
    public Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        => Inner.TranslateAsync(segments, sourceLanguage, targetLanguage, cancellationToken);
    public Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string context, CancellationToken cancellationToken = default) => Inner.RewriteAsync(html, mode, context, cancellationToken);
    public Task<WinoSyncSnapshotDownload?> GetSyncSnapshotAsync(CancellationToken cancellationToken = default) => Inner.GetSyncSnapshotAsync(cancellationToken);
    public Task<UserSyncSnapshotStatusDto> PutSyncSnapshotAsync(byte[] payload, long? expectedRevision = null, CancellationToken cancellationToken = default)
        => Inner.PutSyncSnapshotAsync(payload, expectedRevision, cancellationToken);
    public Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default) => Inner.GetMailboxesAsync(cancellationToken);
    public Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default) => Inner.ReplaceMailboxesAsync(request, cancellationToken);
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Inner.SignOutAsync(cancellationToken);
}
#endif
