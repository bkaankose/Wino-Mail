using System.Net;

namespace Wino.Mail.Uwp.Activation;

internal sealed class NotificationArguments
{
    private static readonly char[] PairSeparators = ['&', ';'];
    private readonly IReadOnlyDictionary<string, string> values;

    private NotificationArguments(IReadOnlyDictionary<string, string> values)
    {
        this.values = values;
    }

    public string this[string key] => values[key];

    public static NotificationArguments Parse(string? encodedArguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (encodedArguments ?? string.Empty).Split(
                     PairSeparators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var key = separatorIndex < 0 ? pair : pair[..separatorIndex];
            var value = separatorIndex < 0 ? string.Empty : pair[(separatorIndex + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }

        return new NotificationArguments(result);
    }

    public bool TryGetValue(string key, out string value)
        => values.TryGetValue(key, out value!);

    public bool TryGetValue<TEnum>(string key, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        return values.TryGetValue(key, out var rawValue) &&
               Enum.TryParse(rawValue, true, out value);
    }
}
