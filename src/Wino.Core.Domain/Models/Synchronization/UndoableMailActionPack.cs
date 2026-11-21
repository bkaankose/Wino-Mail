using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>Which surface shows an undo pack.</summary>
public enum UndoableActionKind
{
    Mail,
    Task
}

public sealed record UndoableMailActionPack(
    Guid Id,
    IReadOnlyList<Guid> AccountIds,
    string Title,
    InfoBarMessageType Severity,
    DateTimeOffset ExpiresAt,
    int IntervalInSeconds)
{
    public UndoableActionKind Kind { get; init; }

    public bool ContainsAccount(Guid accountId) => AccountIds.Contains(accountId);
}
