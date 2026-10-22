using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sentry;

namespace Wino.Services;

/// <summary>
/// Removes encoded secrets from everything Wino sends to Sentry. Protocol exceptions can echo a
/// server's view of the client's last command; for IMAP AUTH PLAIN that is the base64 of
/// "\0user\0password", so any long base64-looking run is replaced before an event leaves the app.
/// </summary>
public static partial class WinoSentryEventScrubber
{
    public const string Replacement = "[redacted]";

    private const int MinimumTokenLength = 16;
    private const int MaximumNestingDepth = 4;

    // Values Wino sets itself. Package names and releases contain long mixed tokens that are not secrets.
    private static readonly HashSet<string> PreservedTagKeys = new(StringComparer.Ordinal)
    {
        "diagnostic_id",
        "app_version",
        "build_configuration",
        "package_name",
        "dist",
        "app_mode",
        "environment",
        "release",
        "operation",
        "event_kind",
        "user_initiated"
    };

    private static readonly HashSet<string> PreservedExtraKeys = new(StringComparer.Ordinal)
    {
        "DiagnosticId",
        "SentryEnvironment",
        "SentryRelease",
        "SentryDist"
    };

    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumTokenLength)
            return value;

        return EncodedTokenRegex().Replace(value, static match => LooksEncoded(match.ValueSpan) ? Replacement : match.Value);
    }

    public static void Scrub(SentryEvent sentryEvent)
    {
        if (sentryEvent.Message is { } message)
        {
            message.Message = Scrub(message.Message);
            message.Formatted = Scrub(message.Formatted);
        }

        if (sentryEvent.SentryExceptions is { } sentryExceptions)
        {
            // Materialize so the edits land on the instances that are serialized.
            var exceptions = sentryExceptions.ToList();

            foreach (var exception in exceptions)
            {
                exception.Value = Scrub(exception.Value);

                if (exception.Mechanism is { } mechanism)
                {
                    mechanism.Description = Scrub(mechanism.Description);
                    ScrubInPlace(mechanism.Data, depth: 0);
                }
            }

            sentryEvent.SentryExceptions = exceptions;
        }

        foreach (var tag in sentryEvent.Tags.ToArray())
        {
            if (PreservedTagKeys.Contains(tag.Key))
                continue;

            var scrubbed = Scrub(tag.Value);
            if (!string.Equals(scrubbed, tag.Value, StringComparison.Ordinal))
                sentryEvent.SetTag(tag.Key, scrubbed ?? string.Empty);
        }

        foreach (var extra in sentryEvent.Extra.ToArray())
        {
            if (PreservedExtraKeys.Contains(extra.Key))
                continue;

            var scrubbed = ScrubValue(extra.Value, depth: 0);
            if (!ReferenceEquals(scrubbed, extra.Value))
                sentryEvent.SetExtra(extra.Key, scrubbed);
        }

        ScrubInPlace(sentryEvent.Contexts, depth: 0);
    }

    public static Breadcrumb Scrub(Breadcrumb breadcrumb)
    {
        var message = Scrub(breadcrumb.Message);
        var isChanged = !string.Equals(message, breadcrumb.Message, StringComparison.Ordinal);

        Dictionary<string, string>? data = null;
        if (breadcrumb.Data is { Count: > 0 } breadcrumbData)
        {
            data = new Dictionary<string, string>(breadcrumbData.Count, StringComparer.Ordinal);

            foreach (var item in breadcrumbData)
            {
                var scrubbed = Scrub(item.Value) ?? string.Empty;
                isChanged |= !string.Equals(scrubbed, item.Value, StringComparison.Ordinal);
                data[item.Key] = scrubbed;
            }
        }

        return isChanged
            ? new Breadcrumb(message ?? string.Empty, breadcrumb.Type ?? string.Empty, data, breadcrumb.Category, breadcrumb.Level)
            : breadcrumb;
    }

    private static void ScrubInPlace(IDictionary<string, object>? values, int depth)
    {
        if (values == null || values.Count == 0)
            return;

        foreach (var item in values.ToArray())
        {
            var scrubbed = ScrubValue(item.Value, depth);
            // Only strings and dictionaries are replaced, and never with null.
            if (scrubbed != null && !ReferenceEquals(scrubbed, item.Value))
                values[item.Key] = scrubbed;
        }
    }

    /// <summary>
    /// Returns the same instance when nothing changed. SDK context objects (device, OS, runtime)
    /// are neither strings nor dictionaries and pass through untouched.
    /// </summary>
    private static object? ScrubValue(object? value, int depth)
    {
        if (depth > MaximumNestingDepth)
            return value;

        switch (value)
        {
            case string text:
            {
                var scrubbed = Scrub(text);
                return string.Equals(scrubbed, text, StringComparison.Ordinal) ? text : scrubbed;
            }
            case IReadOnlyDictionary<string, string> stringMap:
            {
                var isChanged = false;
                var copy = new Dictionary<string, string>(stringMap.Count, StringComparer.Ordinal);

                foreach (var item in stringMap)
                {
                    var scrubbed = Scrub(item.Value) ?? string.Empty;
                    isChanged |= !string.Equals(scrubbed, item.Value, StringComparison.Ordinal);
                    copy[item.Key] = scrubbed;
                }

                return isChanged ? copy : value;
            }
            case IDictionary<string, object> objectMap:
            {
                var isChanged = false;
                var copy = new Dictionary<string, object?>(objectMap.Count, StringComparer.Ordinal);

                foreach (var item in objectMap)
                {
                    var scrubbed = ScrubValue(item.Value, depth + 1);
                    isChanged |= !ReferenceEquals(scrubbed, item.Value);
                    copy[item.Key] = scrubbed;
                }

                return isChanged ? copy : value;
            }
            default:
                return value;
        }
    }

    /// <summary>
    /// Decides whether a run of base64 alphabet characters is encoded data rather than prose,
    /// identifiers or versions. Errs on the side of scrubbing: any run that mixes letters and
    /// digits or carries '+' or '/' is removed. Letter-only runs are removed when both cases make
    /// up at least a third of the letters, which random base64 does and PascalCase type names
    /// do not. Hyphenated GUIDs are kept so correlation identifiers survive.
    /// </summary>
    private static bool LooksEncoded(ReadOnlySpan<char> token)
    {
        if (token.Length == 36 && Guid.TryParseExact(token, "D", out _))
            return false;

        var core = token.TrimEnd('=');
        int digits = 0, upper = 0, lower = 0;

        foreach (var character in core)
        {
            if (character is '+' or '/')
                return true;

            if (char.IsAsciiDigit(character))
                digits++;
            else if (char.IsAsciiLetterUpper(character))
                upper++;
            else if (char.IsAsciiLetterLower(character))
                lower++;
        }

        var letters = upper + lower;
        if (letters == 0)
            return false;

        if (digits > 0)
            return true;

        return upper * 3 >= letters && lower * 3 >= letters;
    }

    // A maximal run of the standard and URL-safe base64 alphabets with optional padding.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_\-])[A-Za-z0-9+/_\-]{16,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedTokenRegex();
}
