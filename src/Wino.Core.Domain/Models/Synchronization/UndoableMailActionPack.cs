using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public sealed record UndoableMailActionPack(
    Guid Id,
    IReadOnlyList<Guid> AccountIds,
    string Title,
    InfoBarMessageType Severity,
    DateTimeOffset ExpiresAt,
    int IntervalInSeconds)
{
    public bool ContainsAccount(Guid accountId) => AccountIds.Contains(accountId);
}
