#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.Contracts.SemanticIndex;

namespace Wino.Services;

internal static class SemanticCoverageRules
{
    internal static IReadOnlyList<T> GetWaitingNewestFirst<T>(
        IEnumerable<T> items,
        Func<T, DateTimeOffset> receivedAtUtc,
        Func<T, string> messageId,
        SemanticMailboxIndexStateDto? state)
        => items
            .Where(item => IsOutsideRange(receivedAtUtc(item), state))
            .OrderByDescending(receivedAtUtc)
            .ThenBy(messageId, StringComparer.Ordinal)
            .ToList();

    internal static bool IsOutsideRange(DateTimeOffset receivedAtUtc, SemanticMailboxIndexStateDto? state)
        => state?.OldestIndexedAtUtc is null ||
           state.NewestIndexedAtUtc is null ||
           receivedAtUtc < state.OldestIndexedAtUtc.Value ||
           receivedAtUtc > state.NewestIndexedAtUtc.Value;

    internal static bool IsRangeCovered(
        IReadOnlyCollection<DateTimeOffset> localDates,
        SemanticMailboxIndexStateDto? state)
        => localDates.Count > 0 &&
           state?.OldestIndexedAtUtc is { } serverOldest &&
           state.NewestIndexedAtUtc is { } serverNewest &&
           serverOldest <= localDates.Min() &&
           serverNewest >= localDates.Max();
}
