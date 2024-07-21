using System;
using System.Collections.Generic;
using Wino.Domain.Enums;
using Wino.Domain.Models.Folders;

namespace Wino.Domain.Models.MailItem
{
    public record MailListInitializationOptions(IEnumerable<IMailItemFolder> Folders,
                                                FilterOptionType FilterType,
                                                SortingOptionType SortingOptionType,
                                                bool CreateThreads,
                                                bool? IsFocusedOnly,
                                                string SearchQuery,
                                                IEnumerable<Guid> ExistingUniqueIds);
}
