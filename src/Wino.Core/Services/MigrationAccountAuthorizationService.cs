using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Core.Services;

public sealed class MigrationAccountAuthorizationService(
    IAccountService accountService,
    IAccountProviderFeatureService featureService,
    IAuthenticationProvider authenticationProvider) : IMigrationAccountAuthorizationService
{
    public async Task AuthenticateAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var account = await accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account no longer exists in the migrated database.");
        EnsureProviderSupportsAuthorization(account);

        account.IsContactAccessGranted = options.EnableContacts;
        account.IsTaskAccessGranted = options.EnableTasks;

        var requestedFeatures = options.EnableMailFilters
            ? new[] { ProviderFeature.MailFilters }
            : Array.Empty<ProviderFeature>();
        var authenticator = authenticationProvider.GetAuthenticator(account.ProviderType);
        if (authenticator is IGmailAuthenticator gmailAuthenticator)
            gmailAuthenticator.ProposeCopyAuthURL = true;

        var token = await authenticator
            .GenerateTokenInformationAsync(account, requestedFeatures)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(token.AccountAddress))
            account.Address = token.AccountAddress;
        if (!string.IsNullOrWhiteSpace(token.AuthenticationAddress))
            account.AuthenticationAddress = token.AuthenticationAddress;

        account.IsContactAccessEnabled = options.EnableContacts;
        account.IsContactReauthorizationRequired = false;
        account.IsTaskAccessEnabled = options.EnableTasks;
        account.IsTaskReauthorizationRequired = false;
        if (account.AttentionReason == AccountAttentionReason.InvalidCredentials)
            account.AttentionReason = AccountAttentionReason.None;

        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);

        if (options.EnableMailFilters)
        {
            var existing = await featureService
                .GetFeatureAsync(account.Id, ProviderFeature.MailFilters, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTime.UtcNow;
            await featureService.UpsertAsync(new AccountProviderFeature
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                MailAccountId = account.Id,
                Feature = ProviderFeature.MailFilters,
                AuthorizationState = ProviderFeatureAuthorizationState.Active,
                EnabledAtUtc = existing?.EnabledAtUtc ?? now,
                LastAuthorizedAtUtc = now
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SkipAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var account = await accountService.GetAccountAsync(options.AccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account no longer exists in the migrated database.");
        EnsureProviderSupportsAuthorization(account);

        account.IsContactAccessEnabled = options.EnableContacts;
        account.IsContactAccessGranted = false;
        account.IsContactReauthorizationRequired = options.EnableContacts;
        account.IsTaskAccessEnabled = options.EnableTasks;
        account.IsTaskAccessGranted = false;
        account.IsTaskReauthorizationRequired = options.EnableTasks;
        account.AttentionReason = AccountAttentionReason.InvalidCredentials;

        await accountService.UpdateAccountAsync(account).ConfigureAwait(false);

        if (options.EnableMailFilters)
        {
            var existing = await featureService
                .GetFeatureAsync(account.Id, ProviderFeature.MailFilters, cancellationToken)
                .ConfigureAwait(false);
            await featureService.UpsertAsync(new AccountProviderFeature
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                MailAccountId = account.Id,
                Feature = ProviderFeature.MailFilters,
                AuthorizationState = ProviderFeatureAuthorizationState.ReauthorizationRequired,
                EnabledAtUtc = existing?.EnabledAtUtc ?? DateTime.UtcNow,
                LastAuthorizedAtUtc = existing?.LastAuthorizedAtUtc
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureProviderSupportsAuthorization(MailAccount account)
    {
        if (account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            throw new NotSupportedException("Only Gmail and Outlook accounts require provider capability authorization.");
    }
}
