#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wino.Services;

/// <summary>
/// iCalendar content lines (RFC 5545 section 3.1) as written by the server, for edits that must keep
/// every line they do not change byte for byte. Each line keeps its original folded text.
/// </summary>
internal static class CalDavIcsLines
{
    private const int MaximumLineOctets = 75;

    public static List<IcsLine> Parse(string icsContent)
    {
        var physical = icsContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var lines = new List<IcsLine>();
        StringBuilder? logical = null;
        StringBuilder? raw = null;

        void Flush()
        {
            if (logical != null && raw != null)
                lines.Add(IcsLine.Create(logical.ToString(), raw.ToString()));

            logical = null;
            raw = null;
        }

        foreach (var line in physical)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && logical != null && raw != null)
            {
                logical.Append(line, 1, line.Length - 1);
                raw.Append("\r\n").Append(line);
                continue;
            }

            Flush();
            if (line.Length == 0)
                continue;

            logical = new StringBuilder(line);
            raw = new StringBuilder(line);
        }

        Flush();
        return lines;
    }

    /// <summary>Components directly inside VCALENDAR (VEVENT, VTIMEZONE, ...), in document order.</summary>
    public static List<IcsBlock> TopLevelComponents(IReadOnlyList<IcsLine> lines)
    {
        var blocks = new List<IcsBlock>();
        var depth = 0;
        IcsBlock? current = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Name == "BEGIN")
            {
                depth++;
                if (depth == 2)
                    current = new IcsBlock(line.Value.Trim().ToUpperInvariant(), index);
                else if (depth == 3 && current != null && current.FirstChildIndex < 0)
                    current.FirstChildIndex = index;

                continue;
            }

            if (line.Name == "END")
            {
                if (depth == 2 && current != null)
                {
                    current.EndIndex = index;
                    blocks.Add(current);
                    current = null;
                }

                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 2 && current != null)
                current.PropertyIndexes.Add(index);
        }

        return blocks;
    }

    /// <summary>The index of the END:VCALENDAR line, or -1.</summary>
    public static int CalendarEndIndex(IReadOnlyList<IcsLine> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (lines[index].Name == "END" && string.Equals(lines[index].Value.Trim(), "VCALENDAR", StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// The raw lines of <paramref name="block"/> with the properties named in <paramref name="changes"/>
    /// replaced. A replacement goes where the first line of that name stood; a property the block did
    /// not have goes before the first subcomponent (VALARM), else before END. A null value removes the
    /// property. Every other line is copied unchanged.
    /// </summary>
    public static IEnumerable<string> PatchBlock(IReadOnlyList<IcsLine> lines, IcsBlock block, IReadOnlyDictionary<string, List<IcsOutputLine>?> changes)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var insertIndex = block.FirstChildIndex >= 0 ? block.FirstChildIndex : block.EndIndex;
        var properties = block.PropertyIndexes.ToHashSet();

        for (var index = block.BeginIndex; index <= block.EndIndex; index++)
        {
            if (index == insertIndex)
            {
                foreach (var (name, replacement) in changes)
                {
                    if (written.Contains(name) || replacement == null)
                        continue;

                    written.Add(name);
                    foreach (var output in replacement)
                        yield return output.ToRaw();
                }
            }

            var line = lines[index];
            if (properties.Contains(index) && changes.TryGetValue(line.Name, out var lineReplacement))
            {
                if (written.Add(line.Name) && lineReplacement != null)
                {
                    foreach (var output in lineReplacement)
                        yield return output.ToRaw();
                }

                continue;
            }

            yield return line.Raw;
        }
    }

    public static string Join(IEnumerable<string> rawLines)
    {
        var builder = new StringBuilder();
        foreach (var raw in rawLines)
            builder.Append(raw).Append("\r\n");

        return builder.ToString();
    }

    /// <summary>Folds at 75 octets (RFC 5545 section 3.1) without splitting a UTF-8 sequence.</summary>
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= MaximumLineOctets)
            return line;

        var builder = new StringBuilder(line.Length + 16);
        var octets = 0;
        var limit = MaximumLineOctets;

        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                builder.Append("\r\n ");
                octets = 0;
                limit = MaximumLineOctets - 1;
            }

            builder.Append(rune.ToString());
            octets += size;
        }

        return builder.ToString();
    }

    /// <summary>TEXT escaping (RFC 5545 section 3.3.11).</summary>
    public static string EscapeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>A parameter value, quoted when it contains ":", ";" or "," (RFC 5545 section 3.2). DQUOTE is not allowed and is dropped.</summary>
    public static string ParameterValue(string value)
    {
        var cleaned = value.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return cleaned.IndexOfAny([':', ';', ',']) >= 0 ? $"\"{cleaned}\"" : cleaned;
    }

    public static string FormatUtc(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>One content line: the unfolded text split into name, parameters and value, and the original folded text.</summary>
internal sealed class IcsLine
{
    public string Name { get; private init; } = string.Empty;
    public string Parameters { get; private init; } = string.Empty;
    public string Value { get; private init; } = string.Empty;
    public string Raw { get; private init; } = string.Empty;

    public static IcsLine Create(string logical, string raw)
    {
        var inQuotes = false;
        var nameEnd = -1;
        var valueStart = -1;

        for (var index = 0; index < logical.Length; index++)
        {
            var character = logical[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
                continue;

            if (nameEnd < 0 && (character == ';' || character == ':'))
                nameEnd = index;

            if (character == ':')
            {
                valueStart = index + 1;
                break;
            }
        }

        if (nameEnd < 0 || valueStart < 0)
            return new IcsLine { Name = logical.Trim().ToUpperInvariant(), Raw = raw };

        var parameters = valueStart - 1 > nameEnd + 1 ? logical[(nameEnd + 1)..(valueStart - 1)] : string.Empty;

        return new IcsLine
        {
            Name = logical[..nameEnd].Trim().ToUpperInvariant(),
            Parameters = parameters,
            Value = logical[valueStart..],
            Raw = raw
        };
    }

    public string? GetParameter(string name)
    {
        foreach (var (parameterName, parameterValue) in SplitParameters())
        {
            if (string.Equals(parameterName, name, StringComparison.OrdinalIgnoreCase))
                return parameterValue.Trim().Trim('"');
        }

        return null;
    }

    /// <summary>Parameters in their written order, values with their quotes.</summary>
    public IEnumerable<(string Name, string Value)> SplitParameters()
    {
        if (string.IsNullOrEmpty(Parameters))
            yield break;

        var start = 0;
        var inQuotes = false;
        for (var index = 0; index <= Parameters.Length; index++)
        {
            if (index < Parameters.Length && Parameters[index] == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (index < Parameters.Length && (Parameters[index] != ';' || inQuotes))
                continue;

            var parameter = Parameters[start..index];
            start = index + 1;
            var separator = parameter.IndexOf('=');
            if (separator > 0)
                yield return (parameter[..separator].Trim(), parameter[(separator + 1)..]);
            else if (parameter.Length > 0)
                yield return (parameter.Trim(), string.Empty);
        }
    }

    /// <summary>
    /// The same property with some parameters replaced, added or removed (a null value removes).
    /// Other parameters keep their order and spelling.
    /// </summary>
    public string WithParameters(IReadOnlyDictionary<string, string?> replacements)
    {
        var builder = new StringBuilder(Name);
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in SplitParameters())
        {
            if (replacements.TryGetValue(name, out var replacement))
            {
                replaced.Add(name);
                if (replacement != null)
                    builder.Append(';').Append(name).Append('=').Append(CalDavIcsLines.ParameterValue(replacement));

                continue;
            }

            builder.Append(';').Append(name);
            if (value.Length > 0)
                builder.Append('=').Append(value);
        }

        foreach (var (name, value) in replacements)
        {
            if (!replaced.Contains(name) && value != null)
                builder.Append(';').Append(name).Append('=').Append(CalDavIcsLines.ParameterValue(value));
        }

        return builder.Append(':').Append(Value).ToString();
    }
}

internal sealed class IcsBlock(string name, int beginIndex)
{
    public string Name { get; } = name;
    public int BeginIndex { get; } = beginIndex;
    public int EndIndex { get; set; } = -1;

    /// <summary>The BEGIN line of the first subcomponent, or -1.</summary>
    public int FirstChildIndex { get; set; } = -1;

    /// <summary>The component's own properties, not those of its subcomponents.</summary>
    public List<int> PropertyIndexes { get; } = [];

    public IEnumerable<IcsLine> Properties(IReadOnlyList<IcsLine> lines) => PropertyIndexes.Select(index => lines[index]);
}

/// <summary>A line to write: either an original line kept as folded, or new unfolded text that is folded on output.</summary>
internal readonly record struct IcsOutputLine(string Text, bool IsRaw)
{
    public static IcsOutputLine New(string logical) => new(logical, false);
    public static IcsOutputLine Keep(IcsLine line) => new(line.Raw, true);
    public string ToRaw() => IsRaw ? Text : CalDavIcsLines.Fold(Text);
}
