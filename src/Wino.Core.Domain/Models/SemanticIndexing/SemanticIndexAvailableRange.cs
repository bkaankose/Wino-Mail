using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public sealed record SemanticIndexAvailableRange(
    DateOnly OldestDate,
    DateOnly NewestDate,
    IReadOnlyDictionary<DateOnly, int> MessageCountsByDate)
{
    public int TotalMessageCount => MessageCountsByDate.Values.Sum();
    public int DaySpan => Math.Max(0, NewestDate.DayNumber - OldestDate.DayNumber);
}
