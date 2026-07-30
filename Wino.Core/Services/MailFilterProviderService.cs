using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

public class MailFilterProviderService(
    ISynchronizerFactory synchronizerFactory,
    IMailFilterService mailFilterService) : IMailFilterProviderService
{
    public bool SupportsProviderFilters(MailAccount account)
        => account?.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;

    public async Task<IReadOnlyList<MailFilter>> GetFiltersAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetSynchronizerAsync(account).ConfigureAwait(false);
        var remoteFilters = await synchronizer.GetProviderFiltersAsync(cancellationToken).ConfigureAwait(false);
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
        var created = await synchronizer.CreateProviderFilterAsync(filter, cancellationToken).ConfigureAwait(false);
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
        var updated = await synchronizer.UpdateProviderFilterAsync(filter, cancellationToken).ConfigureAwait(false);
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
        await synchronizer.DeleteProviderFilterAsync(filter.RemoteId, cancellationToken).ConfigureAwait(false);
        await mailFilterService.DeleteFilterAsync(filter.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IProviderMailFilterSynchronizer> GetSynchronizerAsync(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!SupportsProviderFilters(account))
            throw new NotSupportedException("This mail provider does not expose manageable server filters.");

        var synchronizer = await synchronizerFactory.GetAccountSynchronizerAsync(account.Id).ConfigureAwait(false);
        return synchronizer as IProviderMailFilterSynchronizer
            ?? throw new NotSupportedException("The account synchronizer does not support provider filters.");
    }
}
