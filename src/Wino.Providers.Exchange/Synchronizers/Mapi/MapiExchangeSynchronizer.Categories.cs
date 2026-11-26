using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Exceptions;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.MAPI.Rules;
using Wino.Providers.Exchange.Synchronizers.Exchange;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

/// <summary>
/// Mail categories over MAPI: PidNameKeywords on each message and the IPM.Configuration.CategoryList FAI message of
/// the Calendar folder. The rules are the EWS synchronizer's; only the transport differs.
/// </summary>
public sealed partial class MapiExchangeSynchronizer
{
    // Named property ids are assigned per mailbox, so the mapping holds for every session of this synchronizer.
    private uint? _keywordsTag;

    private async Task<uint> ResolveKeywordsTagAsync(MapiSession session, CancellationToken cancellationToken)
        => _keywordsTag ??= await MapiRulesOperations.ResolveKeywordsTagAsync(session, cancellationToken).ConfigureAwait(false);

    protected override async Task<ExchangeCategoryList?> ReadCategoryListAsync(CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var calendarFolderId = await FindCategoryListFolderIdAsync(session, cancellationToken).ConfigureAwait(false);
        var message = await MapiCategoryOperations.FindCategoryListAsync(session, calendarFolderId, cancellationToken).ConfigureAwait(false);
        if (message is null)
            return null;

        var xml = await MapiCategoryOperations.ReadCategoryListXmlAsync(session, calendarFolderId, message, cancellationToken).ConfigureAwait(false);
        return xml is null ? null : ExchangeCategoryList.Parse(xml);
    }

    protected override async Task EditCategoryListAsync(Func<ExchangeCategoryList?, ExchangeCategoryList?> edit, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var calendarFolderId = await FindCategoryListFolderIdAsync(session, cancellationToken).ConfigureAwait(false);
        var message = await MapiCategoryOperations.FindCategoryListAsync(session, calendarFolderId, cancellationToken).ConfigureAwait(false);
        var xml = message is null ? null : await MapiCategoryOperations.ReadCategoryListXmlAsync(session, calendarFolderId, message, cancellationToken).ConfigureAwait(false);

        var edited = edit(xml is null ? null : ExchangeCategoryList.Parse(xml));
        if (edited is null)
            return;

        if (edited.IsEmpty)
        {
            if (message is not null && !await MapiMessageOperations.DeleteAsync(session, calendarFolderId, [message.MessageId], cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The server did not delete the category list message (RopDeleteMessages PartialCompletion).");

            return;
        }

        await MapiCategoryOperations.WriteCategoryListXmlAsync(session, calendarFolderId, message, edited.Serialize(), cancellationToken).ConfigureAwait(false);
    }

    protected override async Task WriteMailCategoriesAsync(IReadOnlyList<MailCategoryWrite> writes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
            return;

        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var keywordsTag = await ResolveKeywordsTagAsync(session, cancellationToken).ConfigureAwait(false);

        foreach (var write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseMapiId(write.MessageId, out var messageId))
                throw new InvalidOperationException($"Mail {write.MessageId} was synchronized over another transport; it is not addressable over MAPI until the next sync.");

            var folderLocalId = write.FolderId;
            if (folderLocalId is null)
            {
                var copies = await ExchangeChangeProcessor.GetMailCopiesAsync([write.MessageId]).ConfigureAwait(false);
                folderLocalId = copies.FirstOrDefault()?.FolderId
                    ?? throw new SynchronizerEntityNotFoundException($"Mail {write.MessageId} is not stored locally.");
            }

            var folder = folders.FirstOrDefault(f => f.Id == folderLocalId)
                ?? throw new SynchronizerEntityNotFoundException($"Folder {folderLocalId} of mail {write.MessageId} is not known locally.");

            try
            {
                await MapiCategoryOperations.SetKeywordsAsync(session, RequireFolderId(folder), messageId, keywordsTag, write.CategoryNames, cancellationToken).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                throw new SynchronizerEntityNotFoundException($"Mail {write.MessageId} no longer exists on the server.");
            }
        }
    }

    private static async Task<ulong> FindCategoryListFolderIdAsync(MapiSession session, CancellationToken cancellationToken)
    {
        var logon = session.Logon
            ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

        return await MapiCalendarOperations.FindCalendarFolderIdAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mailbox does not name a Calendar folder (PidTagIpmAppointmentEntryId), where its category list lives.");
    }
}
