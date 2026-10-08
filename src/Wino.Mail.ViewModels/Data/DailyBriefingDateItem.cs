#nullable enable
using System;

namespace Wino.Mail.ViewModels;

/// <summary>
/// One day in the briefing's date strip: Today, Yesterday, or the date itself.
/// </summary>
public sealed class DailyBriefingDateItem
{
    public DateOnly Date { get; init; }

    public string DisplayName { get; init; } = string.Empty;
}
