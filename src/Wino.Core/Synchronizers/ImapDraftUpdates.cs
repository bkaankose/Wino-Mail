using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services.Extensions;

namespace Wino.Core.Synchronizers.Mail;

public partial class ImapSynchronizer
{
    private const string DraftRevisionHeader = "X-Wino-Draft-Revision";
    private sealed record Replacement(string Marker, UniqueId OldUid, uint Validity, string Folder, Guid LocalFolder);
    private readonly ConcurrentDictionary<Guid, Replacement> _draftReplacements = new();

    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot,
        MailCopy draft, CancellationToken cancellationToken = default)
    {
        // An interrupted APPEND may have committed. Resolve it before attempting another one.
        if (_draftReplacements.ContainsKey(snapshot.UniqueId))
        {
            var recovered = await RecoverDraftReplacementAsync(snapshot.UniqueId, draft, cancellationToken).ConfigureAwait(false);
            if (recovered != null) recovered.Apply(draft);
        }

        var client = await _clientPool.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var destroy = false;
        try
        {
            return await UpdateDraftOnClientAsync(client, snapshot, draft, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            destroy = true;
            throw;
        }
        finally
        {
            _clientPool.Release(client, destroy);
            if (destroy && _draftReplacements.ContainsKey(snapshot.UniqueId))
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await RecoverDraftReplacementAsync(snapshot.UniqueId, draft, recovery.Token).ConfigureAwait(false); }
                catch (Exception) { /* Keep the marker: the next save must reconcile before appending. */ }
            }
        }
    }

    internal async Task<DraftUpdateIdentity> UpdateDraftOnClientAsync(IImapClient client, DraftUpdateSnapshot snapshot,
        MailCopy draft, CancellationToken cancellationToken)
    {
        var folder = await client.GetFolderAsync(draft.AssignedFolder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
        if (draft.ImapUidValidity == 0 || folder.UidValidity != draft.ImapUidValidity)
            throw new InvalidOperationException("Draft UIDVALIDITY changed.");

        var oldUid = GetUniqueId(draft);
        var existing = await folder.SearchAsync(SearchQuery.Uids([oldUid]), cancellationToken).ConfigureAwait(false);
        if (!existing.Contains(oldUid)) throw new InvalidOperationException("Remote draft no longer exists.");

        using var mime = snapshot.OpenMime();
        var replacement = new Replacement(Guid.NewGuid().ToString("N"), oldUid, folder.UidValidity,
            draft.AssignedFolder.RemoteFolderId, draft.FolderId);
        mime.Headers[DraftRevisionHeader] = replacement.Marker;
        _draftReplacements[snapshot.UniqueId] = replacement;

        UniqueId? newUid;
        if (client.Capabilities.HasFlag(ImapCapabilities.Replace))
            newUid = await folder.ReplaceAsync(oldUid, new ReplaceRequest(mime, MessageFlags.Draft), cancellationToken).ConfigureAwait(false);
        else
            newUid = await folder.AppendAsync(new AppendRequest(mime, MessageFlags.Draft), cancellationToken).ConfigureAwait(false);

        // Once APPEND/REPLACE completed, finish identity bookkeeping despite supersession.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        newUid ??= await FindReplacementAsync(folder, replacement, completion.Token).ConfigureAwait(false);
        if (!newUid.HasValue) throw new InvalidOperationException("Draft replacement identity is uncertain.");
        var identity = await CommitReplacementAsync(client, folder, snapshot.UniqueId, draft, replacement, newUid.Value, completion.Token).ConfigureAwait(false);
        await folder.CloseAsync(false, completion.Token).ConfigureAwait(false);
        return identity;
    }

    private static async Task<UniqueId?> FindReplacementAsync(IMailFolder folder, Replacement replacement, CancellationToken token)
    {
        var matches = await folder.SearchAsync(SearchQuery.HeaderContains(DraftRevisionHeader, replacement.Marker), token).ConfigureAwait(false);
        if (matches.Count > 1) throw new InvalidOperationException("Draft replacement is ambiguous.");
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<DraftUpdateIdentity> RecoverDraftReplacementAsync(Guid uniqueId, MailCopy draft, CancellationToken token)
    {
        if (!_draftReplacements.TryGetValue(uniqueId, out var replacement)) return null;
        var client = await _clientPool.GetClientAsync(token).ConfigureAwait(false);
        var destroy = false;
        try
        {
            var folder = await client.GetFolderAsync(replacement.Folder, token).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, token).ConfigureAwait(false);
            if (folder.UidValidity != replacement.Validity) throw new InvalidOperationException("Draft UIDVALIDITY changed.");
            var newUid = await FindReplacementAsync(folder, replacement, token).ConfigureAwait(false);
            DraftUpdateIdentity identity = null;
            if (newUid.HasValue)
                identity = await CommitReplacementAsync(client, folder, uniqueId, draft, replacement, newUid.Value, token).ConfigureAwait(false);
            else
            {
                var old = await folder.SearchAsync(SearchQuery.Uids([replacement.OldUid]), token).ConfigureAwait(false);
                if (!old.Contains(replacement.OldUid)) throw new InvalidOperationException("Remote draft no longer exists.");
                _draftReplacements.TryRemove(uniqueId, out _);
            }
            await folder.CloseAsync(false, token).ConfigureAwait(false);
            return identity;
        }
        catch { destroy = true; throw; }
        finally { _clientPool.Release(client, destroy); }
    }

    private async Task<DraftUpdateIdentity> CommitReplacementAsync(IImapClient client, IMailFolder folder,
        Guid uniqueId, MailCopy draft, Replacement replacement, UniqueId uid, CancellationToken token)
    {
        var identity = new DraftUpdateIdentity(MailkitClientExtensions.CreateUid(replacement.LocalFolder, uid.Id),
            draft.DraftId, draft.ThreadId, uid.Id, replacement.Validity);
        await _imapChangeProcessor.UpdateDraftIdentityAsync(Account.Id, uniqueId, identity).ConfigureAwait(false);
        if (replacement.OldUid != uid)
        {
            await folder.StoreAsync([replacement.OldUid], new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, token).ConfigureAwait(false);
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                await folder.ExpungeAsync([replacement.OldUid], token).ConfigureAwait(false);
        }
        _draftReplacements.TryRemove(uniqueId, out _);
        return identity;
    }
}
