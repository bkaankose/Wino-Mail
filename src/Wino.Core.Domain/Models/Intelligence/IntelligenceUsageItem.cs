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

    // The contracts package has no voice bucket id yet; the API reports voice commands under this name.
    public const string VoiceBucket = "voice";

    private static readonly string[] DisplayOrder =
    [
        AiQuotaBucketIds.Intelligence,
        AiQuotaBucketIds.Summarize,
        AiQuotaBucketIds.Translate,
        AiQuotaBucketIds.Rewrite,
        VoiceBucket
    ];

    /// <summary>
    /// Every bucket the API reports, known buckets in a fixed order and unknown ones after them.
    /// Empty when the user has no entitlement.
    /// </summary>
    public static IReadOnlyList<IntelligenceUsageItem> Describe(AiUsageStatusDto? usage)
    {
        if (usage is null)
        {
            return Array.Empty<IntelligenceUsageItem>();
        }

        return usage.Buckets
            .Select(bucket => new IntelligenceUsageItem(bucket.Bucket, DisplayName(bucket.Bucket), bucket.Used, bucket.Limit))
            .OrderBy(item => DisplayRank(item.Bucket))
            .ToArray();
    }

    private static int DisplayRank(string bucket)
    {
        var index = Array.IndexOf(DisplayOrder, bucket);
        return index < 0 ? DisplayOrder.Length : index;
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
        VoiceBucket => Translator.WinoIntelligence_UsageBucketVoice,
        _ => bucket,
    };
}
