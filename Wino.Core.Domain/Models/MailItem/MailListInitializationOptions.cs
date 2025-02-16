using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Models.MailItem;

public record MailListInitializationOptions(IEnumerable<IMailItemFolder> Folders,
                                            FilterOptionType FilterType,
                                            SortingOptionType SortingOptionType,
                                            bool CreateThreads,
                                            bool? IsFocusedOnly,
                                            string SearchQuery,
                                            IEnumerable<Guid> ExistingUniqueIds);
