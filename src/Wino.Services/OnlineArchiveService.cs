using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Services;

/// <summary>
/// Thin, read-only orchestrator over the account's synchronizer for the Exchange online archive. Holds no
/// state and no cache; the archive folder tree is walked lazily and mail is fetched live, nothing is
/// persisted into the mailbox tables. Mirrors <see cref="PublicFolderService"/>.
/// </summary>
public class OnlineArchiveService : IOnlineArchiveService
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public OnlineArchiveService(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public bool SupportsOnlineArchive(MailAccount account) => account?.ProviderType == MailProviderType.Exchange;

    public async Task<IReadOnlyList<PublicFolderNode>> GetRootFoldersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetOnlineArchiveChildrenAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicFolderNode>> GetChildrenAsync(Guid accountId, string parentFolderId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetOnlineArchiveChildrenAsync(parentFolderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailCopy>> GetMailItemsAsync(Guid accountId, string folderId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetOnlineArchiveMailItemsAsync(folderId, skip, take, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetMailMimeAsync(Guid accountId, string folderId, string itemId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetOnlineArchiveMailMimeAsync(folderId, itemId, cancellationToken).ConfigureAwait(false);
    }

    private Task<IWinoSynchronizerBase> GetCapableSynchronizerAsync(Guid accountId)
        => SynchronizerCapabilityGate.RequireAsync(
            _synchronizerFactory,
            accountId,
            synchronizer => synchronizer.SupportsOnlineArchive,
            "The online archive is only supported for Exchange accounts.");
}
