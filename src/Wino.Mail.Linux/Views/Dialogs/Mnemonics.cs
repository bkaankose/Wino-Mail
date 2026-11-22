using System.Text;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// Alt+letter mnemonics for dialog buttons: each label gets an underscore before its first letter that no earlier label
/// uses, and literal underscores are doubled so translated text never turns into a mnemonic by accident.
/// </summary>
internal static class Mnemonics
{
    public static string[] Assign(IReadOnlyList<string> labels)
    {
        var used = new HashSet<char>();
        var result = new string[labels.Count];
        for (var index = 0; index < labels.Count; index++) result[index] = Assign(labels[index], used);
        return result;
    }

    /// <summary>One label against the letters <paramref name="used"/> already holds; a label without a free letter gets none.</summary>
    public static string Assign(string label, HashSet<char> used)
    {
        var escaped = label.Replace("_", "__", StringComparison.Ordinal);
        for (var index = 0; index < escaped.Length; index++)
        {
            var character = escaped[index];
            if (!char.IsLetterOrDigit(character) || !used.Add(char.ToLowerInvariant(character))) continue;
            return new StringBuilder(escaped.Length + 1).Append(escaped, 0, index).Append('_').Append(escaped, index, escaped.Length - index).ToString();
        }
        return escaped;
    }
}
