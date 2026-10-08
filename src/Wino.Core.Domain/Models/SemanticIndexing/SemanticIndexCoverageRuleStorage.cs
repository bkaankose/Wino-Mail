#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wino.Core.Domain.Models.SemanticIndexing;

/// <summary>
/// Serializes per-folder coverage rules to and from the single text column they live in.
/// </summary>
/// <remarks>
/// A delimited line format rather than JSON, to match the other intelligence preference columns and
/// to stay clear of the source-generated serializer contexts the AOT build depends on.
/// <para>
/// The folder id comes last on each line and is never escaped, because provider folder ids are
/// opaque and may contain the field separator. Splitting with a field limit hands whatever remains
/// to the id verbatim, so only a newline inside an id could corrupt a line — and a newline already
/// separates the folder id list stored beside this one.
/// </para>
/// </remarks>
public static class SemanticIndexCoverageRuleStorage
{
    private const char FieldSeparator = '|';
    private const int FieldCount = 6;

    public static string Serialize(IEnumerable<SemanticIndexFolderCoverageRule> rules)
    {
        var lines = new List<string>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RemoteFolderId))
                continue;
            lines.Add(SerializeRule(rule));
        }
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<SemanticIndexFolderCoverageRule> Deserialize(string? value)
    {
        var rules = new List<SemanticIndexFolderCoverageRule>();
        if (string.IsNullOrWhiteSpace(value))
            return rules;

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // A malformed line is dropped rather than thrown on: a preference column that fails to
            // parse would otherwise take the whole account load down with it.
            if (TryParseRule(line, out var rule))
                rules.Add(rule);
        }
        return rules;
    }

    /// <summary>
    /// Serializes the account-wide default, which carries no folder id of its own.
    /// </summary>
    public static string SerializeDefault(SemanticIndexFolderCoverageRule rule)
        => SerializeRule(rule with { RemoteFolderId = string.Empty });

    /// <returns>The stored default, or <paramref name="fallback"/> when nothing is stored.</returns>
    public static SemanticIndexFolderCoverageRule DeserializeDefault(
        string? value, SemanticIndexFolderCoverageRule fallback)
        => TryParseRule((value ?? string.Empty).Trim(), out var rule)
            ? rule with { RemoteFolderId = string.Empty }
            : fallback;

    private static string SerializeRule(SemanticIndexFolderCoverageRule rule) => string.Join(
        FieldSeparator,
        rule.Mode == SemanticIndexCoverageMode.LatestCount ? "count" : "date",
        rule.DatePreset.ToStableId(),
        TicksOrEmpty(rule.CutoffUtc),
        TicksOrEmpty(rule.ThroughUtcExclusive),
        rule.LatestMessageCount.ToString(CultureInfo.InvariantCulture),
        rule.RemoteFolderId);

    private static bool TryParseRule(string line, out SemanticIndexFolderCoverageRule rule)
    {
        rule = default!;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var fields = line.Split(FieldSeparator, FieldCount);
        if (fields.Length < FieldCount)
            return false;

        var mode = fields[0] == "count"
            ? SemanticIndexCoverageMode.LatestCount
            : SemanticIndexCoverageMode.DateRange;

        // An unknown preset id falls back to OnlyNew rather than failing the line, which is the
        // same choice FromStableId already makes for every other caller.
        rule = new SemanticIndexFolderCoverageRule(
            fields[5],
            mode,
            SemanticIndexRangePresetExtensions.FromStableId(fields[1]),
            ParseTicks(fields[2]),
            ParseTicks(fields[3]),
            int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? Math.Max(0, count)
                : 0);
        return true;
    }

    private static string TicksOrEmpty(DateTimeOffset? value)
        => value is null ? string.Empty : value.Value.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTicks(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= 0
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;
}
