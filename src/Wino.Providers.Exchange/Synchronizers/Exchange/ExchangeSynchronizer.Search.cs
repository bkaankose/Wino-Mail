using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Task = System.Threading.Tasks.Task;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// Online search over EWS FindItem, one shallow search per requested folder. Text criteria go into the AQS QueryString
/// (MS-OXWSSRCH 3.1.4.2.3.2; not in Exchange 2007, which this schema version never reaches). A QueryString excludes a
/// restriction, so the other filters are applied to the stored results by MailSearchFilterMatcher. Without text, the
/// filters are a FindItem restriction instead. Results the mailbox does not hold locally yet are stored as the folder
/// sync stores them.
/// </summary>
public partial class ExchangeSynchronizer
{
    private const int MaxOnlineSearchResultsPerFolder = 1000;

    // PidTagFlagStatus (MS-OXOFLAG 2.2.1.1): 2 is followupFlagged. EWS has no searchable flag field of its own.
    private static readonly ExtendedPropertyDefinition FlagStatusProperty = new(0x1090, MapiPropertyType.Integer);

    public override async Task<List<MailCopy>> OnlineSearchAsync(RemoteMailSearchCriteria criteria, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        var localFolders = (await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
            .ToDictionary(folder => folder.Id);
        var searchFolders = (folders ?? [])
            .Where(folder => folder != null && folder.MailAccountId == Account.Id)
            .Select(folder => localFolders.TryGetValue(folder.Id, out var local) ? local : null)
            .OfType<MailItemFolder>()
            .Where(folder => !string.IsNullOrEmpty(folder.RemoteFolderId))
            .DistinctBy(folder => folder.Id)
            .ToList();

        if (searchFolders.Count == 0)
            return [];

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var queryString = BuildEwsQueryString(criteria);
        var filter = queryString == null ? BuildEwsSearchFilter(criteria) : null;
        var resultIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in searchFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var view = new ItemView(MaxOnlineSearchResultsPerFolder)
            {
                PropertySet = ItemMetadataPropertySet,
                Traversal = ItemTraversal.Shallow
            };
            view.OrderBy.Add(ItemSchema.DateTimeReceived, SortDirection.Descending);

            var folderId = new FolderId(folder.RemoteFolderId);
            var results = queryString != null
                ? await service.FindItems(folderId, queryString, view, cancellationToken).ConfigureAwait(false)
                : filter != null
                    ? await service.FindItems(folderId, filter, view, cancellationToken).ConfigureAwait(false)
                    : await service.FindItems(folderId, view, cancellationToken).ConfigureAwait(false);

            var items = results.Items.Where(item => !string.IsNullOrEmpty(item.Id?.UniqueId)).ToList();
            if (items.Count == 0)
                continue;

            var existing = new HashSet<string>(
                await ExchangeChangeProcessor.AreMailsExistsAsync(items.Select(item => item.Id.UniqueId)).ConfigureAwait(false),
                StringComparer.Ordinal);

            foreach (var item in items)
            {
                resultIds.Add(item.Id.UniqueId);
                if (existing.Contains(item.Id.UniqueId))
                    continue;

                foreach (var package in await CreateNewMailPackagesAsync(item, folder, cancellationToken).ConfigureAwait(false))
                    await ExchangeChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
            }

            _logger.Debug("EWS online search in {Folder}: {Count} result(s), {New} stored now.", folder.FolderName, items.Count, items.Count(item => !existing.Contains(item.Id.UniqueId)));
        }

        return resultIds.Count == 0 ? [] : await ExchangeChangeProcessor.GetMailCopiesAsync(resultIds).ConfigureAwait(false);
    }

    /// <summary>The AQS for the text criteria: free text as typed, sender and subject as quoted property terms. Null without text.</summary>
    internal static string? BuildEwsQueryString(RemoteMailSearchCriteria criteria)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(criteria.Query)) terms.Add(criteria.Query.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.Sender)) terms.Add($"from:{ToAqsValue(criteria.Sender)}");
        if (!string.IsNullOrWhiteSpace(criteria.Subject)) terms.Add($"subject:{ToAqsValue(criteria.Subject)}");
        return terms.Count == 0 ? null : string.Join(" ", terms);
    }

    // A quoted AQS value is one phrase; a quote inside it would end the phrase early.
    private static string ToAqsValue(string value) => "\"" + value.Trim().Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    /// <summary>The FindItem restriction for searches without text; null when there is no filter either.</summary>
    internal static SearchFilter? BuildEwsSearchFilter(RemoteMailSearchCriteria criteria)
    {
        var filters = new List<SearchFilter>();
        if (criteria.ReceivedAfterUtc is { } after)
            filters.Add(new SearchFilter.IsGreaterThanOrEqualTo(ItemSchema.DateTimeReceived, after.UtcDateTime));
        if (criteria.ReceivedBeforeUtc is { } before)
            filters.Add(new SearchFilter.IsLessThan(ItemSchema.DateTimeReceived, before.UtcDateTime));
        if (criteria.ReadStatus == MailReadStatusFilter.Unread)
            filters.Add(new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, false));
        if (criteria.ReadStatus == MailReadStatusFilter.Read)
            filters.Add(new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, true));
        if (criteria.HasAttachments)
            filters.Add(new SearchFilter.IsEqualTo(ItemSchema.HasAttachments, true));
        if (criteria.IsFlagged)
            filters.Add(new SearchFilter.IsEqualTo(FlagStatusProperty, 2));

        return filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => new SearchFilter.SearchFilterCollection(LogicalOperator.And, filters)
        };
    }
}
