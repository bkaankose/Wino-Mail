#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// One quota bucket as the UI shows it. The server counts actions rather than money, so
/// this carries counts: "1,240 of 1,500", which a person can act on, instead of a
/// percentage of a budget they were never told the size of.
/// </summary>
public sealed record IntelligenceUsageItem(string Bucket, string DisplayName, int Used, int Limit)
{
    public int Remaining => Math.Max(Limit - Used, 0);

    public bool IsExhausted => Used >= Limit;

    /// <summary>Only for the progress bar. Never shown as a number.</summary>
    public double Percentage => Limit <= 0 ? 0 : Math.Min(Used * 100d / Limit, 100d);

    public string Value => string.Format(Translator.WinoIntelligence_UsageBucketValue, Used, Limit);
}

public static class IntelligenceUsage
{
    /// <summary>
    /// Fixed display order: the bucket that gates indexing first, then the reader
    /// features in the order someone meets them.
    /// </summary>
    private static readonly string[] Order =
    [
        AiQuotaBucketIds.Intelligence,
        AiQuotaBucketIds.Summarize,
        AiQuotaBucketIds.Rewrite,
        AiQuotaBucketIds.Translate,
    ];

    public static IReadOnlyList<IntelligenceUsageItem> Describe(AiUsageStatusDto? usage)
    {
        if (usage is null || usage.Buckets.Count == 0)
        {
            return [];
        }

        return [.. Order
            .Select(bucket => usage.Find(bucket))
            .Where(bucket => bucket is not null)
            .Select(bucket => new IntelligenceUsageItem(
                bucket!.Bucket, DisplayName(bucket.Bucket), bucket.Used, bucket.Limit))];
    }

    /// <summary>
    /// The headline the usage button shows. Mail messages, because that is the bucket
    /// indexing spends and the one a user will hit first.
    /// </summary>
    public static IntelligenceUsageItem? Headline(AiUsageStatusDto? usage)
        => Describe(usage).FirstOrDefault(x => x.Bucket == AiQuotaBucketIds.Intelligence);

    private static string DisplayName(string bucket) => bucket switch
    {
        AiQuotaBucketIds.Intelligence => Translator.WinoIntelligence_UsageBucketIntelligence,
        AiQuotaBucketIds.Summarize => Translator.WinoIntelligence_UsageBucketSummarize,
        AiQuotaBucketIds.Rewrite => Translator.WinoIntelligence_UsageBucketRewrite,
        AiQuotaBucketIds.Translate => Translator.WinoIntelligence_UsageBucketTranslate,
        _ => bucket,
    };
}
