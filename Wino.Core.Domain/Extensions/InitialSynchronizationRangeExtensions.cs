using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class InitialSynchronizationRangeExtensions
{
    public static DateTime? ToCutoffDateUtc(this InitialSynchronizationRange range, DateTime utcNow)
    {
        var normalizedUtcNow = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : utcNow.ToUniversalTime();

        return range switch
        {
            InitialSynchronizationRange.ThreeMonths => normalizedUtcNow.AddMonths(-3),
            InitialSynchronizationRange.SixMonths => normalizedUtcNow.AddMonths(-6),
            InitialSynchronizationRange.NineMonths => normalizedUtcNow.AddMonths(-9),
            InitialSynchronizationRange.OneYear => normalizedUtcNow.AddYears(-1),
            _ => null
        };
    }
}
