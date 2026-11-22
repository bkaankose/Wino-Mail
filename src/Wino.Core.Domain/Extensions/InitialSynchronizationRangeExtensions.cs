using System;
using Wino.Core.Domain.Entities.Shared;
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

    /// <summary>The oldest date the range keeps, counted from when the account was added; null keeps everything.</summary>
    public static DateTime? GetMailSynchronizationCutoffUtc(this MailAccount account, InitialSynchronizationRange range)
        => range.ToCutoffDateUtc(account.CreatedAt ?? DateTime.UtcNow);

    /// <summary>The enum values are not in span order (SixMonths is 0), so ranges are compared by span.</summary>
    public static bool IsWiderThan(this InitialSynchronizationRange range, InitialSynchronizationRange other)
        => GetSpan(range) > GetSpan(other);

    private static int GetSpan(InitialSynchronizationRange range) => range switch
    {
        InitialSynchronizationRange.ThreeMonths => 3,
        InitialSynchronizationRange.SixMonths => 6,
        InitialSynchronizationRange.NineMonths => 9,
        InitialSynchronizationRange.OneYear => 12,
        InitialSynchronizationRange.Everything => int.MaxValue,
        _ => 0
    };
}
