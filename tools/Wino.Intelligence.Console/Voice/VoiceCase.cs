using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wino.Intelligence.ConsoleApp.Voice;

/// <summary>
/// One prompt of a corpus file (JSON lines). <see cref="Answers"/> are what the user picks for each question, in order:
/// the first candidate whose title, detail, address or account contains the text, else the first result of typing it
/// into the filter. Without answers the first candidate is picked. <see cref="Expect"/> is optional.
/// </summary>
internal sealed class VoiceCase
{
    public string Id { get; set; } = string.Empty;
    public string Domain { get; set; } = "misc";
    public string Text { get; set; } = string.Empty;
    public string? Mode { get; set; }
    public List<string>? Answers { get; set; }
    public VoiceExpectation? Expect { get; set; }

    /// <summary>Replaces <see cref="Expect"/> in a Private run (questions the device asks there).</summary>
    public VoiceExpectation? ExpectPrivate { get; set; }

    /// <summary>Replaces <see cref="Expect"/> in a Personal run (names the brain resolves, learned facts).</summary>
    public VoiceExpectation? ExpectPersonal { get; set; }

    public string? Note { get; set; }

    /// <summary>A context-aware case: the selection the request is about, sent in the start frame.</summary>
    public Wino.Mail.Api.Contracts.Voice.VoiceSelectionContext? Selection { get; set; }

    public VoiceExpectation? ExpectFor(Wino.Mail.Api.Contracts.Voice.VoiceMode mode)
        => (mode == Wino.Mail.Api.Contracts.Voice.VoiceMode.Personal ? ExpectPersonal : ExpectPrivate) ?? Expect;
}

internal sealed class VoiceExpectation
{
    /// <summary>The planned actions in order. Several lists: any of them passes.</summary>
    public List<List<string>>? Actions { get; set; }

    /// <summary>The question kinds asked, in order (empty: no question).</summary>
    public List<string>? Questions { get; set; }

    /// <summary>Operation kinds recorded while running, in order; a kind matches by prefix ("Mail" matches "Mail.Archive").</summary>
    public List<string>? Ops { get; set; }

    /// <summary>Texts that some recorded operation's description contains (a resolved address, a title).</summary>
    public List<string>? OpsContain { get; set; }

    /// <summary>A preview group's account (name or address) contains this: where the plan actually runs.</summary>
    public string? Account { get; set; }

    /// <summary>The outcome line starts with this ("Ran", "Nothing to run", "Closed").</summary>
    public string? Outcome { get; set; }

    public List<VoiceCheck>? Checks { get; set; }
}

/// <summary>
/// A check on the plan JSON (camelCase, string enums) after the answers were applied. <see cref="Path"/> is like
/// <c>commands[0].event.startLocal</c>; <c>commands[*]</c> passes when any command passes. Dates are relative to the
/// clock the prompt was planned with: <see cref="Date"/> is "+N" days or "yyyy-MM-dd", <see cref="Weekday"/> a day name
/// within the next 7 days (today included), <see cref="Time"/> "HH:mm".
/// </summary>
internal sealed class VoiceCheck
{
    public string Path { get; set; } = string.Empty;
    public string? Contains { get; set; }
    public List<string>? ContainsAny { get; set; }
    public string? NotContains { get; set; }
    public List<string>? In { get; set; }
    public bool? Empty { get; set; }
    public int? Count { get; set; }
    public int? MinCount { get; set; }
    public string? Date { get; set; }
    public string? Weekday { get; set; }
    public string? Time { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    [JsonPropertyName("equals")]
    public JsonNode? EqualsJson { get; set; }

    public string Describe()
    {
        var parts = new List<string>();
        if (EqualsJson is not null) parts.Add($"= {EqualsJson.ToJsonString()}");
        if (Contains is not null) parts.Add($"contains \"{Contains}\"");
        if (ContainsAny is not null) parts.Add($"contains any [{string.Join(", ", ContainsAny)}]");
        if (NotContains is not null) parts.Add($"not contains \"{NotContains}\"");
        if (In is not null) parts.Add($"in [{string.Join(", ", In)}]");
        if (Empty is not null) parts.Add(Empty.Value ? "empty" : "not empty");
        if (Count is not null) parts.Add($"count {Count}");
        if (MinCount is not null) parts.Add($"count >= {MinCount}");
        if (Date is not null) parts.Add($"date {Date}");
        if (Weekday is not null) parts.Add($"weekday {Weekday}");
        if (Time is not null) parts.Add($"time {Time}");
        if (Min is not null) parts.Add($">= {Min}");
        if (Max is not null) parts.Add($"<= {Max}");
        return $"{Path} {string.Join(", ", parts)}";
    }
}

internal sealed record CheckResult(string Name, bool Passed, string? Actual);

internal static class VoiceCaseReader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static List<VoiceCase> Read(IEnumerable<string> files)
    {
        var cases = new List<VoiceCase>();
        foreach (var file in files)
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                try
                {
                    var item = JsonSerializer.Deserialize<VoiceCase>(trimmed, Options) ?? throw new JsonException("empty");
                    if (string.IsNullOrWhiteSpace(item.Id))
                        item.Id = $"{Path.GetFileNameWithoutExtension(file)}:{number}";
                    cases.Add(item);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException($"{file}:{number}: {exception.Message}");
                }
            }
        }

        return cases;
    }
}

internal static class VoiceCaseChecker
{
    public static List<CheckResult> Evaluate(VoiceExpectation expect, JsonNode plan, IReadOnlyList<string> questionKinds, IReadOnlyList<RecordedOperation> operations, string? outcome, IReadOnlyList<string?> groupAccounts, DateTime now)
    {
        var results = new List<CheckResult>();
        var actions = plan["commands"]?.AsArray().Select(static command => command?["action"]?.GetValue<string>() ?? "?").ToList() ?? [];

        if (expect.Actions is { Count: > 0 } expected)
        {
            var passed = expected.Any(list => list.SequenceEqual(actions, StringComparer.OrdinalIgnoreCase));
            results.Add(new CheckResult($"actions {string.Join(" | ", expected.Select(static list => string.Join(",", list)))}", passed, string.Join(",", actions)));
        }

        if (expect.Questions is { } questions)
            results.Add(new CheckResult($"questions [{string.Join(",", questions)}]", questions.SequenceEqual(questionKinds, StringComparer.OrdinalIgnoreCase), $"[{string.Join(",", questionKinds)}]"));

        var opKinds = operations.Select(static op => op.Kind).ToList();
        foreach (var wanted in expect.OpsContain ?? [])
        {
            var passed = operations.Any(op => Fold(op.Kind + " " + op.Description).Contains(Fold(wanted), StringComparison.Ordinal));
            results.Add(new CheckResult($"an op contains \"{wanted}\"", passed, operations.Count == 0 ? "no ops" : string.Join(" | ", operations.Select(static op => Describer.Clip(op.Kind + ": " + op.Description, 140)))));
        }

        if (expect.Account is { } wantedAccount)
            results.Add(new CheckResult($"runs in account \"{wantedAccount}\"", groupAccounts.Any(account => Fold(account).Contains(Fold(wantedAccount), StringComparison.Ordinal)), string.Join(" | ", groupAccounts.Select(static account => account ?? "none"))));

        if (expect.Outcome is { } wantedOutcome)
            results.Add(new CheckResult($"outcome {wantedOutcome}", outcome?.StartsWith(wantedOutcome, StringComparison.OrdinalIgnoreCase) == true, outcome ?? "none"));

        if (expect.Ops is { } ops)
        {
            var passed = ops.Count == opKinds.Count && ops.Zip(opKinds).All(static pair => pair.Second.StartsWith(pair.First, StringComparison.OrdinalIgnoreCase));
            results.Add(new CheckResult($"ops [{string.Join(",", ops)}]", passed, $"[{string.Join(",", opKinds)}]"));
        }

        foreach (var check in expect.Checks ?? [])
        {
            var nodes = Select(plan, check.Path);
            var passed = false;
            string? actual = null;
            foreach (var node in nodes)
            {
                actual = node?.ToJsonString() ?? "null";
                if (Passes(check, node, now))
                {
                    passed = true;
                    break;
                }
            }

            if (nodes.Count == 0)
                passed = Passes(check, null, now);
            results.Add(new CheckResult(check.Describe(), passed, actual ?? "missing"));
        }

        return results;
    }

    /// <summary>Follows a path; <c>[*]</c> fans out to every element.</summary>
    public static List<JsonNode?> Select(JsonNode root, string path)
    {
        var current = new List<JsonNode?> { root };
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw;
            var indexes = new List<string>();
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                foreach (var part in segment[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries))
                    indexes.Add(part.TrimEnd(']'));
                segment = segment[..bracket];
            }

            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                if (node is not JsonObject obj)
                    continue;

                var property = obj.FirstOrDefault(pair => string.Equals(pair.Key, segment, StringComparison.OrdinalIgnoreCase));
                if (property.Key is null)
                    continue;

                var values = new List<JsonNode?> { property.Value };
                foreach (var index in indexes)
                {
                    var expanded = new List<JsonNode?>();
                    foreach (var value in values)
                    {
                        if (value is not JsonArray array)
                            continue;
                        if (index == "*")
                            expanded.AddRange(array);
                        else if (int.TryParse(index, out var position) && position >= 0 && position < array.Count)
                            expanded.Add(array[position]);
                    }

                    values = expanded;
                }

                next.AddRange(values);
            }

            current = next;
        }

        return current;
    }

    private static bool Passes(VoiceCheck check, JsonNode? node, DateTime now)
    {
        var text = node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            JsonArray array => string.Join(" | ", array.Select(static item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : item?.ToJsonString())),
            _ => node.ToJsonString(),
        };

        if (check.Empty is { } empty)
        {
            var isEmpty = node is null || (node is JsonArray a && a.Count == 0) || (text is not null && text.Length == 0)
                || (node is JsonValue v && v.GetValueKind() == JsonValueKind.Null);
            if (isEmpty != empty)
                return false;
        }

        if (check.EqualsJson is { } expectedJson)
        {
            if (node is null)
                return false;
            if (expectedJson is JsonValue ev && ev.TryGetValue<string>(out var expectedText))
            {
                if (!string.Equals(Fold(text), Fold(expectedText), StringComparison.Ordinal))
                    return false;
            }
            else if (!JsonNode.DeepEquals(node, expectedJson))
            {
                return false;
            }
        }

        if (check.Contains is { } contains && (text is null || !Fold(text).Contains(Fold(contains), StringComparison.Ordinal)))
            return false;
        if (check.ContainsAny is { } any && (text is null || !any.Any(option => Fold(text).Contains(Fold(option), StringComparison.Ordinal))))
            return false;
        if (check.NotContains is { } notContains && text is not null && Fold(text).Contains(Fold(notContains), StringComparison.Ordinal))
            return false;
        if (check.In is { } options && (text is null || !options.Any(option => Fold(option) == Fold(text))))
            return false;

        if (check.Count is not null || check.MinCount is not null)
        {
            var count = node is JsonArray array ? array.Count : node is null ? 0 : 1;
            if (check.Count is { } exact && count != exact)
                return false;
            if (check.MinCount is { } min && count < min)
                return false;
        }

        if (check.Min is not null || check.Max is not null)
        {
            if (node is not JsonValue number || !number.TryGetValue<double>(out var valueNumber))
                return false;
            if (check.Min is { } low && valueNumber < low)
                return false;
            if (check.Max is { } high && valueNumber > high)
                return false;
        }

        if (check.Date is not null || check.Weekday is not null || check.Time is not null)
        {
            if (text is null || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                if (text is null || !DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var dateOnly))
                    return false;
                value = dateOnly.ToDateTime(TimeOnly.MinValue);
            }

            if (check.Date is { } date && value.Date != ResolveDate(date, now))
                return false;

            if (check.Weekday is { } weekday)
            {
                if (!Enum.TryParse<DayOfWeek>(weekday, ignoreCase: true, out var day) || value.DayOfWeek != day)
                    return false;
                var days = (value.Date - now.Date).TotalDays;
                if (days < 0 || days > 7)
                    return false;
            }

            if (check.Time is { } time && value.ToString("HH:mm", CultureInfo.InvariantCulture) != time)
                return false;
        }

        return true;
    }

    public static DateTime ResolveDate(string token, DateTime now)
    {
        if (token.StartsWith('+') || token.StartsWith('-'))
            return now.Date.AddDays(int.Parse(token, CultureInfo.InvariantCulture));
        return DateTime.ParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Lower case without accents, so "Müller" matches "muller" and Turkish dotted letters match.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = text.Replace('ı', 'i').Replace('İ', 'i').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
