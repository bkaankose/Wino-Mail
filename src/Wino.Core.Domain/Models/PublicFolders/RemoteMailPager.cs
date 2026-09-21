using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// Paging state for one listing of a read-only remote folder (public folder or online archive).
/// The server is asked for consecutive <c>skip/take</c> windows, newest first. The items are transient
/// and get a new local identity on every fetch, so repeats are recognised by their server id.
/// </summary>
public sealed class RemoteMailPager
{
    /// <summary>First and following page size while the folder is simply browsed.</summary>
    public const int BrowsePageSize = 100;

    /// <summary>
    /// Page size while a filter or a search narrows the rows locally. A larger window keeps the list
    /// from running dry, which would leave nothing to scroll and so nothing to ask for the next page.
    /// </summary>
    public const int FilteredPageSize = 500;

    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);

    public RemoteMailPager(bool isNarrowedLocally)
    {
        PageSize = isNarrowedLocally ? FilteredPageSize : BrowsePageSize;
    }

    public int PageSize { get; }

    /// <summary>How many server rows were consumed so far, which is the next request's skip.</summary>
    public int Offset { get; private set; }

    /// <summary>False once the server returned a short page.</summary>
    public bool HasMore { get; private set; } = true;

    /// <summary>
    /// Records one fetched window and returns the items not handed out before, in server order.
    /// </summary>
    public List<MailCopy> Accept(IReadOnlyList<MailCopy> fetched)
    {
        ArgumentNullException.ThrowIfNull(fetched);

        Offset += fetched.Count;
        HasMore = fetched.Count >= PageSize;

        var fresh = new List<MailCopy>(fetched.Count);

        foreach (var item in fetched)
        {
            if (item == null)
                continue;

            var key = string.IsNullOrEmpty(item.Id) ? item.UniqueId.ToString("N") : item.Id;

            if (_seenIds.Add(key))
            {
                fresh.Add(item);
            }
        }

        return fresh;
    }
}
