namespace Wino.Core.Domain.Enums;

/// <summary>
/// Semantic color of a briefing tile. The panel keeps categories readable at a glance by mapping
/// each one onto a Fluent system fill color instead of inventing per-category palettes.
/// </summary>
public enum DailyBriefingTone
{
    Neutral,
    Attention,
    Caution,
    Critical,
    Success,
}
