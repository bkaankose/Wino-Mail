#nullable enable
using System;
using System.Text.RegularExpressions;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Pulls the one-time code out of a message body.
/// Classification reports that a message carries a code, but not the code itself, so the card
/// reads it here when the user asks for it rather than storing another copy of the
/// message content.
/// </summary>
public static partial class VerificationCodeExtractor
{
    /// <summary>How far from the keyword a candidate token may sit and still belong to it.</summary>
    private const int ContextWindow = 80;

    [GeneratedRegex(@"\b(verification|confirmation|security|one[- ]?time|access|login|sign[- ]?in|passcode|otp|pin|2fa|code)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordRegex();

    /// <summary>
    /// Digits, or an uppercase alphanumeric block. Mixed-case words are excluded so ordinary
    /// prose next to the keyword is never mistaken for a code.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])([0-9]{4,8}|[0-9]{3}-[0-9]{3}|[A-Z0-9]{5,8})(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidateRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    /// <summary>The code, or null when the body does not state one near a code keyword.</summary>
    public static string? TryExtract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        foreach (Match keyword in KeywordRegex().Matches(body))
        {
            // A code usually follows its keyword ("your code is 123456"), but it can precede
            // it just as often ("123456 is your verification code"), so both sides are read.
            var afterStart = keyword.Index + keyword.Length;
            var after = Window(body, afterStart, Math.Min(ContextWindow, body.Length - afterStart));
            if (FirstCandidate(after) is { } following)
            {
                return following;
            }

            var beforeStart = Math.Max(0, keyword.Index - ContextWindow);
            var before = Window(body, beforeStart, keyword.Index - beforeStart);
            if (LastCandidate(before) is { } preceding)
            {
                return preceding;
            }
        }

        return null;
    }

    /// <summary>Plain text for a body that may only exist as HTML.</summary>
    public static string ToPlainText(string? html)
        => string.IsNullOrWhiteSpace(html) ? string.Empty : HtmlTagRegex().Replace(html, " ");

    private static string Window(string body, int start, int length)
        => length <= 0 ? string.Empty : body.Substring(start, length);

    private static string? FirstCandidate(string text)
    {
        foreach (Match match in CandidateRegex().Matches(text))
        {
            if (IsCode(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string? LastCandidate(string text)
    {
        string? last = null;
        foreach (Match match in CandidateRegex().Matches(text))
        {
            if (IsCode(match.Value))
            {
                last = match.Value;
            }
        }

        return last;
    }

    /// <summary>
    /// Rejects the numbers that sit next to code wording without being one: a year, and a
    /// four-digit value that is really a time or a quantity is not worth guarding against
    /// because the keyword context already excludes most of them.
    /// </summary>
    private static bool IsCode(string value)
    {
        if (value.Length == 4 && int.TryParse(value, out var number) && number is >= 1900 and <= 2200)
        {
            return false;
        }

        return true;
    }
}
