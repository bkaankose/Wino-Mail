using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Models.MailItem;

public static class PreviewTextFilter
{
    private static string _rawPatterns;
    private static List<Regex> _compiledPatterns = [];
    private static bool _initialized;
    private static Func<string> _patternProvider;

    public static int Version { get; private set; }

    public static bool HasPatterns => _compiledPatterns.Count > 0 || (!_initialized && _patternProvider != null);

    public static void Initialize(Func<string> patternProvider)
    {
        _patternProvider = patternProvider;
    }

    public static void SetPatterns(string newlineDelimitedPatterns)
    {
        _initialized = true;

        if (_rawPatterns == newlineDelimitedPatterns) return;

        _rawPatterns = newlineDelimitedPatterns;
        _compiledPatterns = [];
        Version++;

        if (string.IsNullOrWhiteSpace(newlineDelimitedPatterns)) return;

        // The UWP/WinUI multi-line TextBox reports line breaks as a bare '\r'
        // (not '\n' or '\r\n'; see microsoft-ui-xaml#1826), so we split on both
        // '\r' and '\n' to reliably get one pattern per line regardless of the source.
        foreach (var line in newlineDelimitedPatterns.Split('\r', '\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            try
            {
                _compiledPatterns.Add(new Regex(trimmed, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)));
            }
            catch (ArgumentException)
            {
            }
        }
    }

    public static Task<string> ApplyAsync(string previewText)
    {
        return Task.Run(() => Apply(previewText));
    }

    public static string Apply(string previewText)
    {
        if (string.IsNullOrEmpty(previewText)) return previewText;

        if (!_initialized && _patternProvider != null)
            SetPatterns(_patternProvider());

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
