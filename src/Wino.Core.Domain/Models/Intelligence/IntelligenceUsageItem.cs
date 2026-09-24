#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// One usage line as the UI shows it. <see cref="Used"/> and <see cref="Limit"/> are percent
/// of the period's budget, which is what the API reports.
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
    public const string IntelligenceBucket = "intelligence";

    /// <summary>
    /// The API reports one share of the period's budget rather than per-feature counts, so
    /// the list holds a single item measured in percent (limit 100).
    /// </summary>
    public static IReadOnlyList<IntelligenceUsageItem> Describe(AiUsageStatusDto? usage)
    {
        if (usage is null)
        {
            return [];
        }

        var used = (int)Math.Round(Math.Clamp(usage.UsagePercentage, 0m, 100m), MidpointRounding.AwayFromZero);
        return [new IntelligenceUsageItem(IntelligenceBucket, Translator.WinoIntelligence_UsageBucketIntelligence, used, 100)];
    }

    public static IntelligenceUsageItem? Headline(AiUsageStatusDto? usage)
        => Describe(usage).FirstOrDefault();
}
