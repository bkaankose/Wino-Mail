using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.MAPI.Restrictions;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

/// <summary>
/// Online search over the folder contents table: each requested folder's table is restricted (RopRestrict,
/// MS-OXCTABL 2.2.2.4) by every criterion, read newest first with the list columns, and the rows not stored yet are
/// stored as the folder sync stores them.
/// </summary>
public sealed partial class MapiExchangeSynchronizer
{
    private const int MaxOnlineSearchResultsPerFolder = 1000;

    public override async Task<List<MailCopy>> OnlineSearchAsync(RemoteMailSearchCriteria criteria, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        var localFolders = (await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
            .ToDictionary(folder => folder.Id);
        var remaining = new Queue<MailItemFolder>((folders ?? [])
            .Where(folder => folder != null && folder.MailAccountId == Account.Id)
            .Select(folder => localFolders.TryGetValue(folder.Id, out var local) ? local : null)
            .OfType<MailItemFolder>()
            .Where(folder => TryParseFolderId(folder, out _))
            .DistinctBy(folder => folder.Id));

        if (remaining.Count == 0)
            return [];

        var restriction = BuildSearchRestriction(criteria);
        var resultIds = new HashSet<string>(StringComparer.Ordinal);

        // As in a sync pass: a kept session the server dropped (ContextNotFound) is replaced once.
        for (var attempt = 0; attempt < 2 && remaining.Count > 0; attempt++)
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (remaining.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folder = remaining.Peek();
                    await SearchFolderAsync(lease.Session, folder, restriction, resultIds, cancellationToken).ConfigureAwait(false);
                    remaining.Dequeue();
                }
            }
            catch (MapiTransportException ex) when (ex.IsContextNotFound && attempt == 0)
            {
                Logger.Information("MAPI session of {Account} no longer exists on the server; reopening it for the search.", Account.Address);
            }
        }

        return resultIds.Count == 0 ? [] : await ExchangeChangeProcessor.GetMailCopiesAsync(resultIds).ConfigureAwait(false);
    }

    private async Task SearchFolderAsync(MapiSession session, MailItemFolder folder, RestrictionNode? restriction, HashSet<string> resultIds, CancellationToken cancellationToken)
    {
        var messages = await MapiMessageOperations.ReadMessageListAsync(session, RequireFolderId(folder), MaxOnlineSearchResultsPerFolder,
            cancellationToken, Diagnostics, restriction).ConfigureAwait(false);
        if (messages.Count == 0)
            return;

        var ids = messages.Select(message => ToMapiId(message.MessageId)).ToList();
        var existing = new HashSet<string>(await ExchangeChangeProcessor.AreMailsExistsAsync(ids).ConfigureAwait(false), StringComparer.Ordinal);

        var stored = 0;
        foreach (var message in messages)
        {
            var id = ToMapiId(message.MessageId);
            resultIds.Add(id);
            if (existing.Contains(id))
                continue;

            if (await ExchangeChangeProcessor.CreateMailAsync(Account.Id, new NewMailItemPackage(MapToMailCopy(message, folder), null, folder.RemoteFolderId)).ConfigureAwait(false))
                stored++;
        }

        Logger.Debug("MAPI online search in {Folder}: {Count} result(s), {New} stored now.", folder.FolderName, messages.Count, stored);
    }

    /// <summary>
    /// The restriction for the criteria (MS-OXCDATA 2.12), null when there is none. Every content and property
    /// restriction is joined with an exist restriction, since their result on a row without the property is undefined
    /// (2.12.4.1, 2.12.9.1). Text matches a substring, ignoring case.
    /// </summary>
    internal static RestrictionNode? BuildSearchRestriction(RemoteMailSearchCriteria criteria)
    {
        var parts = new List<RestrictionNode>();

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            var text = criteria.Query.Trim();
            // Subject and body, as IMAP searches them. Exchange SE answers MAPI_E_TOO_COMPLEX (0x80040117) when a body
            // restriction is combined with sender properties, under OR or AND.
            parts.Add(RestrictionNode.Or(Contains(PropertyTags.Subject, text), Contains(PropertyTags.Body, text)));
        }

        // With text, the sender filter is applied to the stored results by MailSearchFilterMatcher instead.
        if (!string.IsNullOrWhiteSpace(criteria.Sender) && string.IsNullOrWhiteSpace(criteria.Query))
        {
            var sender = criteria.Sender.Trim();
            parts.Add(RestrictionNode.Or(
                Contains(PropertyTags.SentRepresentingName, sender),
                Contains(PropertyTags.SentRepresentingSmtpAddress, sender),
                Contains(PropertyTags.SenderName, sender),
                Contains(PropertyTags.SenderSmtpAddress, sender)));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Subject))
            parts.Add(Contains(PropertyTags.Subject, criteria.Subject.Trim()));

        if (criteria.ReceivedAfterUtc is { } after)
            parts.Add(Compare(RestrictionOps.RelOpGe, PropertyTags.MessageDeliveryTime, after.UtcDateTime));
        if (criteria.ReceivedBeforeUtc is { } before)
            parts.Add(Compare(RestrictionOps.RelOpLt, PropertyTags.MessageDeliveryTime, before.UtcDateTime));

        // PidTagMessageFlags (MS-OXCMSG 2.2.1.6): mfRead 0x01, mfHasAttach 0x10. PidTagFlagStatus (MS-OXOFLAG 2.2.1.1): 2 is followupFlagged.
        if (criteria.ReadStatus == MailReadStatusFilter.Unread)
            parts.Add(RestrictionNode.BitMask(RestrictionOps.BitMaskEqZ, PropertyTags.MessageFlags, PropertyTags.MessageFlagRead));
        if (criteria.ReadStatus == MailReadStatusFilter.Read)
            parts.Add(RestrictionNode.BitMask(RestrictionOps.BitMaskNeZ, PropertyTags.MessageFlags, PropertyTags.MessageFlagRead));
        if (criteria.HasAttachments)
            parts.Add(RestrictionNode.BitMask(RestrictionOps.BitMaskNeZ, PropertyTags.MessageFlags, PropertyTags.MessageFlagHasAttach));
        if (criteria.IsFlagged)
            parts.Add(Compare(RestrictionOps.RelOpEq, PropertyTags.FlagStatus, 2u));

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => RestrictionNode.And([.. parts])
        };
    }

    private static RestrictionNode Contains(uint tag, string value)
        => RestrictionNode.And(RestrictionNode.Exist(tag), RestrictionNode.Content(tag, value, RestrictionOps.FuzzySubString, RestrictionOps.FuzzyIgnoreCase));

    private static RestrictionNode Compare(byte relOp, uint tag, object value)
        => RestrictionNode.And(RestrictionNode.Exist(tag), RestrictionNode.Property(relOp, tag, value));
}
