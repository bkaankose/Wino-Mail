using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Models.Contacts;

public record PagedContactsResult(
    IReadOnlyList<AccountContact> Contacts,
    int TotalCount,
    bool HasMore,
    int Offset,
    int PageSize);
