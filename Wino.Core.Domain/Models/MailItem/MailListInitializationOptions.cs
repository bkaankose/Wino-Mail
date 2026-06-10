using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

// Folders is the concrete entity list: this record crosses the UI <-> companion pipe and
// interface-typed members cannot be deserialized.
public record MailListInitializationOptions(List<MailItemFolder> Folders,
                                            FilterOptionType FilterType,
                                            SortingOptionType SortingOptionType,
                                            bool CreateThreads,
                                            bool? IsFocusedOnly,
                                            string SearchQuery,
                                            ConcurrentDictionary<Guid, bool> ExistingUniqueIds = null,
                                            List<MailCopy> PreFetchMailCopies = null,
                                            bool DeduplicateByServerId = false,
                                            int Skip = 0,
                                            int Take = 0)
{
    public IReadOnlyList<Guid> CategoryIds { get; init; }
    public bool IsCategoryView => CategoryIds?.Count > 0;
}
