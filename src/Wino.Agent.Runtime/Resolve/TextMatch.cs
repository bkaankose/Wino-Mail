using System.Globalization;
using System.Text;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>
/// Structural text matching, the only kind the runtime does: folding (case, accents, dotless i), word splitting,
/// prefix and equality of words. No word lists, no language tables; language understanding is the reading's.
/// </summary>
public static class TextMatch
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var n = text.Replace('ı', 'i').Replace('İ', 'i').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString().Trim();
    }

    /// <summary>Folded words: runs of letters and digits.</summary>
    public static string[] Words(string? text)
    {
        var folded = Fold(text);
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }

        if (sb.Length > 0) words.Add(sb.ToString());
        return words.ToArray();
    }

    /// <summary>Letters and digits only, folded: the key of a pick ("Burak's" and "burak" are the same words).</summary>
    public static string Key(string? text) => string.Concat(Words(text));

    /// <summary>Whether the letters of <paramref name="text"/> appear in <paramref name="request"/> (case, accents, spaces and punctuation ignored).</summary>
    public static bool Quoted(string? request, string? text)
    {
        var needle = Key(text);
        return needle.Length > 0 && Key(request).Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>
    /// How well query words name a thing with the given name words: 1 when the word sets are equal; 0.6 when every
    /// query word is a prefix (3+ letters, or exact) of a distinct name word; 0 otherwise. Address local parts count
    /// as name words.
    /// </summary>
    public static double NameScore(string[] query, string[] name)
    {
        if (query.Length == 0 || name.Length == 0) return 0;
        if (query.Length == name.Length && query.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(name.OrderBy(x => x, StringComparer.Ordinal))) return 1;
        var used = new bool[name.Length];
        foreach (var q in query)
        {
            var found = false;
            for (var i = 0; i < name.Length; i++)
            {
                if (used[i]) continue;
                if (name[i] == q || (q.Length >= 3 && name[i].StartsWith(q, StringComparison.Ordinal)) || (q.Length >= 5 && q.StartsWith(name[i], StringComparison.Ordinal) && name[i].Length >= 4))
                {
                    used[i] = true;
                    found = true;
                    break;
                }
            }

            if (!found) return 0;
        }

        return 0.6;
    }

    /// <summary>Equality of folded texts.</summary>
    public static bool Same(string? a, string? b) => Fold(a) == Fold(b) && Fold(a).Length > 0;

    /// <summary>Whether <paramref name="name"/> contains the folded <paramref name="query"/> (3+ letters).</summary>
    public static bool Contains(string? name, string? query)
    {
        var q = Fold(query);
        return q.Length >= 3 && Fold(name).Contains(q, StringComparison.Ordinal);
    }

    public static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 ? address[(at + 1)..].ToLowerInvariant() : address.ToLowerInvariant();
    }

    public static string LocalPart(string address)
    {
        var at = address.IndexOf('@');
        return at >= 0 ? address[..at] : address;
    }

    public static bool LooksLikeAddress(string? text) => text is not null && text.Contains('@') && text.IndexOf('@') > 0 && text.IndexOf('.', text.IndexOf('@')) > 0 && !text.Contains(' ');

    public static bool LooksLikeDomain(string? text) => text is not null && !text.Contains('@') && !text.Contains(' ') && text.Contains('.') && !text.StartsWith('.') && !text.EndsWith('.');

    /// <summary>A concept value for the brain: 1-60 letters (spaces allowed), no digits, links, addresses or markup.</summary>
    public static bool IsConcept(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60) return false;
        if (value.Contains('@') || value.Contains('/') || value.Contains('<') || value.Contains('>') || value.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;
        return value.All(ch => char.IsLetter(ch) || ch == ' ' || ch == '-' || ch == '\'') && value.Any(char.IsLetter);
    }
}
