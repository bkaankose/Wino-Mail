using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Services;

/// <summary>
/// Thin, read-only orchestrator over the account's synchronizer for Exchange public folders. Holds no state
/// and no cache; the hierarchy is walked lazily and content is fetched live, nothing is persisted into the
/// mailbox tables. Mirrors <see cref="GlobalAddressListService"/>: resolves the synchronizer via
/// <see cref="ISynchronizerFactory"/>, gates on the capability, and delegates.
/// </summary>
public class PublicFolderService : IPublicFolderService
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public PublicFolderService(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public bool SupportsPublicFolders(MailAccount account) => account?.ProviderType == MailProviderType.Exchange;

    public async Task<IReadOnlyList<PublicFolderNode>> GetRootChildrenAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderChildrenAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicFolderNode>> GetChildrenAsync(Guid accountId, string parentFolderId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderChildrenAsync(parentFolderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailCopy>> GetMailItemsAsync(Guid accountId, string folderId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderMailItemsAsync(folderId, skip, take, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetMailMimeAsync(Guid accountId, string folderId, string itemId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderMailMimeAsync(folderId, itemId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalendarItem>> GetAppointmentsAsync(Guid accountId, string folderId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderAppointmentsAsync(folderId, startUtc, endUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicFolderContact>> GetContactsAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetPublicFolderContactsAsync(folderId, cancellationToken).ConfigureAwait(false);
    }

    private Task<IWinoSynchronizerBase> GetCapableSynchronizerAsync(Guid accountId)
        => SynchronizerCapabilityGate.RequireAsync(
            _synchronizerFactory,
            accountId,
            synchronizer => synchronizer.SupportsPublicFolders,
            "Public folders are only supported for Exchange accounts.");
}
