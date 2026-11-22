using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Interfaces;

namespace Wino.Core.Services;

public class ProviderFeatureAuthorizationService(
    IAccountProviderFeatureService featureService,
    IAccountService accountService,
    IAuthenticationProvider authenticationProvider,
    ISynchronizerFactory synchronizerFactory,
    IMailFilterService mailFilterService,
    IMailProviderRegistry providers) : IProviderFeatureAuthorizationService
{
    public bool IsSupported(MailAccount account, ProviderFeature feature)
        => account != null && feature switch
        {
            ProviderFeature.MailFilters => providers.GetTraits(account.ProviderType).SupportsProviderMailFilters,
            ProviderFeature.CrossAccountMoveImport => providers.GetTraits(account.ProviderType).MailImportRequiresAuthorization,
            _ => false
        };

    public Task<AccountProviderFeature> GetFeatureAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
        => featureService.GetFeatureAsync(accountId, feature, cancellationToken);

    public async Task EnableAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account no longer exists.");
        if (!IsSupported(account, feature))
            throw new NotSupportedException("This provider does not support the requested optional feature.");

        var existingFeatures = await featureService.GetFeaturesAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (feature == ProviderFeature.CrossAccountMoveImport)
        {
            // Its token is for another resource than the account's synchronization token, so it is authorized in a
            // request of its own and never added to the account's sign-in scopes.
            if (authenticationProvider.GetAuthenticator(account.ProviderType) is not IMailImportTokenProvider importTokens)
                throw new NotSupportedException("This provider does not support the requested optional feature.");

            await importTokens.AuthorizeMailImportAsync(account, cancellationToken).ConfigureAwait(false);
            await UpsertActiveAsync(accountId, feature, existingFeatures, cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestedFeatures = existingFeatures
            .Select(item => item.Feature)
            .Append(feature)
            .Distinct()
            .ToArray();

        var authenticator = authenticationProvider.GetAuthenticator(account.ProviderType);

        var token = await authenticator
            .GenerateTokenInformationAsync(account, requestedFeatures, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(token.AccountAddress))
            account.Address = token.AccountAddress;
        if (!string.IsNullOrWhiteSpace(token.AuthenticationAddress))
            account.AuthenticationAddress = token.AuthenticationAddress;

        // Verify the provider accepted the new permission before recording the opt-in.
        if (feature == ProviderFeature.MailFilters)
        {
            var synchronizer = await synchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false);
            if (synchronizer is not IProviderMailFilterSynchronizer filterSynchronizer)
                throw new NotSupportedException("The account synchronizer does not support provider filters.");

            var filters = await filterSynchronizer.GetProviderFiltersAsync(cancellationToken).ConfigureAwait(false);
            await mailFilterService.ReplaceProviderFiltersAsync(accountId, filters, cancellationToken).ConfigureAwait(false);
        }

        await UpsertActiveAsync(accountId, feature, existingFeatures, cancellationToken).ConfigureAwait(false);

        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
    }

    private Task UpsertActiveAsync(
        Guid accountId,
        ProviderFeature feature,
        IReadOnlyList<AccountProviderFeature> existingFeatures,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = existingFeatures.FirstOrDefault(item => item.Feature == feature);
        return featureService.UpsertAsync(new AccountProviderFeature
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            MailAccountId = accountId,
            Feature = feature,
            AuthorizationState = ProviderFeatureAuthorizationState.Active,
            EnabledAtUtc = existing?.EnabledAtUtc ?? now,
            LastAuthorizedAtUtc = now
        }, cancellationToken);
    }

    public Task DisableAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
        => featureService.DeleteAsync(accountId, feature, cancellationToken);

    public async Task MarkReauthorizationRequiredAsync(
        Guid accountId,
        ProviderFeature feature,
        CancellationToken cancellationToken = default)
    {
        var existing = await featureService.GetFeatureAsync(accountId, feature, cancellationToken).ConfigureAwait(false);
        if (existing == null)
            return;

        existing.AuthorizationState = ProviderFeatureAuthorizationState.ReauthorizationRequired;
        await featureService.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
    }
}
