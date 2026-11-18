#nullable enable
using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Platform;

/// <summary>
/// When an app asks for a store rating through a system review prompt (macOS and iOS). The first trigger only starts
/// the clock, the first request waits <see cref="FirstRequestDelay"/>, and later requests wait <see cref="RequestInterval"/>.
/// Windows keeps its own rule in <c>MicrosoftStoreService</c>.
/// </summary>
/// <param name="firstTriggerKey">Each platform keeps its own keys; they are stored settings, so they never change.</param>
public sealed class StoreRatingRequestSchedule(IConfigurationService configurationService, string firstTriggerKey, string latestRequestKey)
{
    /// <summary>Time between the first rating trigger and the first request, so new users are not asked.</summary>
    public static readonly TimeSpan FirstRequestDelay = TimeSpan.FromDays(3);

    /// <summary>The system also limits prompts (three a year); this keeps Wino's own requests rare.</summary>
    public static readonly TimeSpan RequestInterval = TimeSpan.FromDays(120);

    /// <summary>True when a review should be requested now; it then records the request time.</summary>
    public bool TryBeginRequest()
    {
        var now = DateTime.UtcNow;
        var firstTrigger = configurationService.Get(firstTriggerKey, DateTime.MinValue);
        if (firstTrigger == DateTime.MinValue)
        {
            configurationService.Set(firstTriggerKey, now);
            return false;
        }

        var latestRequest = configurationService.Get(latestRequestKey, DateTime.MinValue);
        if (now - firstTrigger < FirstRequestDelay || now - latestRequest < RequestInterval) return false;

        configurationService.Set(latestRequestKey, now);
        return true;
    }
}
