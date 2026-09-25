#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// One usage line as the UI shows it: how many of a monthly bucket's actions are used, for
/// example 400 of 1,500 intelligence messages.
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
    public const string IntelligenceBucket = AiQuotaBucketIds.Intelligence;

    /// <summary>
    /// Every bucket the API reports, in its order. Empty when the user has no entitlement.
    /// </summary>
    public static IReadOnlyList<IntelligenceUsageItem> Describe(AiUsageStatusDto? usage)
    {
        if (usage is null)
        {
            return [];
        }

        return usage.Buckets
            .Select(bucket => new IntelligenceUsageItem(bucket.Bucket, DisplayName(bucket.Bucket), bucket.Used, bucket.Limit))
            .ToArray();
    }

    /// <summary>The intelligence bucket, which is what indexing and analysis spend.</summary>
    public static IntelligenceUsageItem? Headline(AiUsageStatusDto? usage)
        => Describe(usage).FirstOrDefault(item => item.Bucket == IntelligenceBucket);

    private static string DisplayName(string bucket) => bucket switch
    {
        AiQuotaBucketIds.Intelligence => Translator.WinoIntelligence_UsageBucketIntelligence,
        AiQuotaBucketIds.Summarize => Translator.WinoIntelligence_UsageBucketSummarize,
        AiQuotaBucketIds.Rewrite => Translator.WinoIntelligence_UsageBucketRewrite,
        AiQuotaBucketIds.Translate => Translator.WinoIntelligence_UsageBucketTranslate,
        _ => bucket,
    };
}
