using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Google;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Identity.Client;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Services;

public class MailFilterProviderService(
    ISynchronizerFactory synchronizerFactory,
    IMailFilterService mailFilterService,
    IAccountProviderFeatureService featureService,
    IProviderFeatureAuthorizationService featureAuthorizationService) : IMailFilterProviderService
{
    public bool SupportsProviderFilters(MailAccount account)
        => account?.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;

    public Task<bool> IsProviderFiltersEnabledAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
        => featureService.IsEnabledAsync(accountId, ProviderFeature.MailFilters, cancellationToken);

    public async Task<IReadOnlyList<MailFilter>> GetFiltersAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetSynchronizerAsync(account).ConfigureAwait(false);
        var remoteFilters = await ExecuteProviderOperationAsync(
            account,
            () => synchronizer.GetProviderFiltersAsync(cancellationToken)).ConfigureAwait(false);
        await mailFilterService
            .ReplaceProviderFiltersAsync(account.Id, remoteFilters, cancellationToken)
            .ConfigureAwait(false);

        return (await mailFilterService.GetFiltersAsync(account.Id, cancellationToken).ConfigureAwait(false))
            .Where(filter => filter.ManagementType == MailFilterManagementType.Provider)
            .ToList();
    }

    public async Task<MailFilter> CreateFilterAsync(
        MailAccount account,
        MailFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var synchronizer = await GetSynchronizerAsync(account).ConfigureAwait(false);
        var created = await ExecuteProviderOperationAsync(
            account,
            () => synchronizer.CreateProviderFilterAsync(filter, cancellationToken)).ConfigureAwait(false);
        created.MailAccountId = account.Id;
        created.ManagementType = MailFilterManagementType.Provider;
        created.IsWinoCreated = true;
        created.IsReadOnly = false;
        return await mailFilterService.CreateFilterAsync(created, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateFilterAsync(
        MailAccount account,
        MailFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.IsWinoCreated || filter.IsReadOnly)
            throw new InvalidOperationException("Imported provider filters cannot be edited by Wino.");

        var synchronizer = await GetSynchronizerAsync(account).ConfigureAwait(false);
        var updated = await ExecuteProviderOperationAsync(
            account,
            () => synchronizer.UpdateProviderFilterAsync(filter, cancellationToken)).ConfigureAwait(false);
        updated.Id = filter.Id;
        updated.MailAccountId = account.Id;
        updated.ManagementType = MailFilterManagementType.Provider;
        updated.IsWinoCreated = true;
        updated.IsReadOnly = false;
        updated.CreatedAtUtc = filter.CreatedAtUtc;
        await mailFilterService.UpdateFilterAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFilterAsync(
        MailAccount account,
        MailFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(filter.RemoteId))
            throw new InvalidOperationException("The provider filter has no remote identifier.");

        // Remote first: a provider failure must leave the local representation intact.
        var synchronizer = await GetSynchronizerAsync(account).ConfigureAwait(false);
        await ExecuteProviderOperationAsync(
            account,
            () => synchronizer.DeleteProviderFilterAsync(filter.RemoteId, cancellationToken)).ConfigureAwait(false);
        await mailFilterService.DeleteFilterAsync(filter.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IProviderMailFilterSynchronizer> GetSynchronizerAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!SupportsProviderFilters(account))
            throw new NotSupportedException("This mail provider does not expose manageable server filters.");
        if (!await IsProviderFiltersEnabledAsync(account.Id).ConfigureAwait(false))
            throw new InvalidOperationException("Provider filters must be connected before they can be used.");

        var synchronizer = await synchronizerFactory.GetAccountSynchronizerAsync(account.Id).ConfigureAwait(false);
        return synchronizer as IProviderMailFilterSynchronizer
            ?? throw new NotSupportedException("The account synchronizer does not support provider filters.");
    }

    private async Task<T> ExecuteProviderOperationAsync<T>(MailAccount account, Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            await featureAuthorizationService
                .MarkReauthorizationRequiredAsync(account.Id, ProviderFeature.MailFilters)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteProviderOperationAsync(MailAccount account, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            await featureAuthorizationService
                .MarkReauthorizationRequiredAsync(account.Id, ProviderFeature.MailFilters)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsAuthorizationFailure(Exception exception)
        => exception is GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            or ODataError { ResponseStatusCode: 401 or 403 }
            or ApiException { ResponseStatusCode: 401 or 403 }
            or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            or AuthenticationAttentionException
            or MsalUiRequiredException;
}
