using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// A pattern that failed to compile, surfaced to the settings UI so the user can fix it.
/// Only produced in regex mode; plain-text patterns are escaped and always compile.
/// </summary>
public readonly record struct PreviewFilterError(int Line, string Pattern, string Message);

public static class PreviewTextFilter
{
    private static string _rawPatterns;
    private static bool _useRegex;
    private static List<Regex> _compiledPatterns = [];
    private static bool _initialized;
    private static Func<string> _patternProvider;
    private static Func<bool> _useRegexProvider;

    public static int Version { get; private set; }

    public static bool HasPatterns => _compiledPatterns.Count > 0 || (!_initialized && _patternProvider != null);

    public static void Initialize(Func<string> patternProvider, Func<bool> useRegexProvider)
    {
        _patternProvider = patternProvider;
        _useRegexProvider = useRegexProvider;
    }

    public static void SetPatterns(string newlineDelimitedPatterns)
    {
        var useRegex = _initialized ? _useRegex : (_useRegexProvider?.Invoke() ?? false);
        Configure(newlineDelimitedPatterns, useRegex);
    }

    public static void SetUseRegex(bool useRegex)
    {
        var patterns = _initialized ? _rawPatterns : _patternProvider?.Invoke();
        Configure(patterns, useRegex);
    }

    /// <summary>
    /// Validates patterns without touching the active filter state, so the settings UI can
    /// show live feedback while the user is still typing. Plain-text patterns are always valid;
    /// only regex mode can produce errors.
    /// </summary>
    public static IReadOnlyList<PreviewFilterError> Validate(string newlineDelimitedPatterns, bool useRegex)
    {
        var errors = new List<PreviewFilterError>();

        if (!useRegex || string.IsNullOrWhiteSpace(newlineDelimitedPatterns)) return errors;

        foreach (var (lineNumber, trimmed) in EnumeratePatternLines(newlineDelimitedPatterns))
        {
            try
            {
                _ = new Regex(trimmed);
            }
            catch (RegexParseException exc)
            {
                errors.Add(new PreviewFilterError(lineNumber, trimmed, exc.Message));
            }
            catch (ArgumentException exc)
            {
                errors.Add(new PreviewFilterError(lineNumber, trimmed, exc.Message));
            }
        }

        return errors;
    }

    private static void Configure(string newlineDelimitedPatterns, bool useRegex)
    {
        if (_initialized && _rawPatterns == newlineDelimitedPatterns && _useRegex == useRegex) return;

        _initialized = true;
        _rawPatterns = newlineDelimitedPatterns;
        _useRegex = useRegex;
        _compiledPatterns = [];
        Version++;

        if (string.IsNullOrWhiteSpace(newlineDelimitedPatterns)) return;

        foreach (var (_, trimmed) in EnumeratePatternLines(newlineDelimitedPatterns))
        {
            // In plain-text mode the pattern is matched literally, so escape every regex
            // metacharacter (e.g. "[EXTERNAL]" matches that literal text, not a char class).
            // In regex mode the pattern is used as the user typed it.
            var effectivePattern = useRegex ? trimmed : Regex.Escape(trimmed);

            try
            {
                _compiledPatterns.Add(new Regex(effectivePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)));
            }
            catch (ArgumentException)
            {
                // Invalid patterns are skipped here; the settings UI reports them via Validate.
            }
        }
    }

    /// <summary>
    /// Yields the 1-based line number and trimmed text of every non-empty, non-comment line.
    /// The UWP/WinUI multi-line TextBox reports line breaks as a bare '\r' (not '\n' or '\r\n';
    /// see microsoft-ui-xaml#1826), so all forms are normalized to '\n' for correct line numbers.
    /// </summary>
    private static IEnumerable<(int lineNumber, string trimmed)> EnumeratePatternLines(string newlineDelimitedPatterns)
    {
        var normalized = newlineDelimitedPatterns.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            yield return (i + 1, trimmed);
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized || _patternProvider == null) return;
        Configure(_patternProvider(), _useRegexProvider?.Invoke() ?? false);
    }

    public static Task<string> ApplyAsync(string previewText)
    {
        return Task.Run(() => Apply(previewText));
    }

    public static string Apply(string previewText)
    {
        if (string.IsNullOrEmpty(previewText)) return previewText;

        EnsureInitialized();

        if (_compiledPatterns.Count == 0) return previewText;

        foreach (var pattern in _compiledPatterns)
        {
            try
            {
                previewText = pattern.Replace(previewText, string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return previewText.Trim();
    }
}
