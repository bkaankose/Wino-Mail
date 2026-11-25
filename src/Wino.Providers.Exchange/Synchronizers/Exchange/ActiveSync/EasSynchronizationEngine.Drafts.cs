using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed partial class EasSynchronizationEngine
{
    /// <summary>
    /// Saves a later version of a server draft with a Change of its fields, so the draft keeps its ServerId. The
    /// attachments the server copy holds are read first: one Wino added with the same content, recorded by the
    /// FileReference the server returned for it, is kept; every other is deleted by its FileReference, and the draft's
    /// attachments not on the server are added. The FileReferences the server returns for the additions are recorded.
    /// </summary>
    internal async Task UpdateServerDraftAsync(string folderId, string serverId, MimeMessage message, bool isRead, bool isFlagged,
        CancellationToken cancellationToken)
    {
        var collection = await GetCollectionAsync(folderId).ConfigureAwait(false);
        var key = EasDataMapper.Key(_account.Id, folderId, serverId);
        var recorded = ReadRecordedAttachments((await _database.Connection.FindAsync<EasDraftState>(key).ConfigureAwait(false))?.Attachments);
        var onServer = await _client.FetchAttachmentReferencesAsync(folderId, serverId, cancellationToken).ConfigureAwait(false);

        var all = EasDraftMapper.Attachments(message);
        var remaining = all.ToList();
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var deleted = new List<string>();
        foreach (var reference in onServer)
        {
            var match = recorded.TryGetValue(reference, out var fingerprint) ? remaining.FirstOrDefault(attachment => attachment.Fingerprint == fingerprint) : null;
            if (match != null)
            {
                remaining.Remove(match);
                kept[reference] = match.Fingerprint;
            }
            else
                deleted.Add(reference);
        }

        var added = remaining.Select((attachment, index) => (ClientId: EasDraftMapper.AttachmentClientId(index), Attachment: attachment)).ToList();
        var data = EasDraftMapper.Change(message, isRead, isFlagged, deleted, added.Select(item => item.Attachment.ToAdd(item.ClientId)));
        var change = EasMutation.Change(serverId, data) with { Effect = EasDraftMapper.ChangeEffect(data, all) };
        var result = (await MutateItemsAsync(folderId, [change], deletesAsMoves: true, cancellationToken).ConfigureAwait(false))[0];
        if (result.Status == "7")
            throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncChangeConflict);
        if (!result.Succeeded)
            throw new EasCommandException("Sync", result.Status);

        foreach (var answer in result.Data?.Element(EasDataMapper.Body + "Attachments")?.Elements(EasDataMapper.Body + "Attachment") ?? [])
        {
            var clientId = answer.Element(EasDataMapper.Body + "ClientId")?.Value;
            var reference = answer.Element(EasDataMapper.Body + "FileReference")?.Value;
            var source = added.FirstOrDefault(item => item.ClientId == clientId).Attachment;
            if (source != null && !string.IsNullOrEmpty(reference))
                kept[reference] = source.Fingerprint;
        }

        await _database.Connection.InsertOrReplaceAsync(new EasDraftState
        {
            Id = key, AccountId = _account.Id, CollectionId = collection.Id, ServerId = serverId,
            Attachments = WriteRecordedAttachments(kept)
        }, typeof(EasDraftState)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the server copy of a draft whose message was sent. The deletion is recorded first and stays recorded
    /// until the server confirms it or reports the item gone, so a failure here is retried before the Drafts folder is
    /// next synchronized, in this session or after a restart.
    /// </summary>
    internal async Task DeleteSentDraftAsync(string folderId, string serverId, CancellationToken cancellationToken)
    {
        var collection = await GetCollectionAsync(folderId).ConfigureAwait(false);
        var key = EasDataMapper.Key(_account.Id, folderId, serverId);
        await _database.Connection.InsertOrReplaceAsync(new EasDraftState
        {
            Id = key, AccountId = _account.Id, CollectionId = collection.Id, ServerId = serverId, DeletePending = true
        }, typeof(EasDraftState)).ConfigureAwait(false);

        try
        {
            var result = (await MutateItemsAsync(folderId, [EasMutation.Delete(serverId)], deletesAsMoves: false, cancellationToken).ConfigureAwait(false))[0];
            if (result.Succeeded || result.IsGone)
                await _database.Connection.DeleteAsync<EasDraftState>(key).ConfigureAwait(false);
            else
                Serilog.Log.Warning("EAS draft {DraftId} of {Account} was sent, but deleting it returned status {Status}; it is deleted again before the next Drafts synchronization.",
                    serverId, _account.Address, result.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Serilog.Log.Warning(exception, "EAS draft {DraftId} of {Account} was sent, but deleting it failed; it is deleted again before the next Drafts synchronization.",
                serverId, _account.Address);
        }
    }

    /// <summary>
    /// Sends the recorded deletions of sent drafts in the collection. Each stays recorded until the server confirms it,
    /// or reports the draft deleted; either way <see cref="DeleteProjectionAsync"/> removes the record with the item.
    /// </summary>
    private async Task SendDraftDeletionsAsync(EasCollection state, CancellationToken cancellationToken)
    {
        var pending = await _database.Connection.Table<EasDraftState>()
            .Where(row => row.CollectionId == state.Id && row.DeletePending).ToListAsync().ConfigureAwait(false);
        if (pending.Count == 0)
            return;

        var results = await SendMutationsAsync(state, pending.Select(row => EasMutation.Delete(row.ServerId)).ToList(), deletesAsMoves: false, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < pending.Count; index++)
        {
            if (results[index].Succeeded)
                await DeleteProjectionAsync(state, pending[index].ServerId).ConfigureAwait(false);
            else
                Serilog.Log.Warning("EAS draft {DraftId} of {Account}: deleting the sent draft returned status {Status}; it is tried again next time.",
                    pending[index].ServerId, _account.Address, results[index].Status);
        }
    }

    private static Dictionary<string, string> ReadRecordedAttachments(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
            return result;
        foreach (var attachment in XElement.Parse(text).Elements("Attachment"))
        {
            var reference = (string?)attachment.Attribute("FileReference");
            var fingerprint = (string?)attachment.Attribute("Fingerprint");
            if (!string.IsNullOrEmpty(reference) && !string.IsNullOrEmpty(fingerprint))
                result[reference] = fingerprint;
        }
        return result;
    }

    private static string WriteRecordedAttachments(Dictionary<string, string> attachments)
        => attachments.Count == 0 ? string.Empty
            : new XElement("Attachments", attachments.Select(pair => new XElement("Attachment", new XAttribute("FileReference", pair.Key), new XAttribute("Fingerprint", pair.Value))))
                .ToString(SaveOptions.DisableFormatting);
}
