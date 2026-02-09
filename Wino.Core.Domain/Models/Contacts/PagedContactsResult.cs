using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

public record PagedContactsResult(
    IReadOnlyList<AccountContact> Contacts,
    int TotalCount,
    bool HasMore,
    int Offset,
    int PageSize);
