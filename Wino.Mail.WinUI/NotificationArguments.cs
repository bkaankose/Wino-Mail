using System;
using System.Collections.Generic;
using System.Net;

namespace Wino.Mail.WinUI;

internal sealed class NotificationArguments
{
    private static readonly NotificationArguments Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> _values;

    private NotificationArguments(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public string this[string key] => _values[key];

    public static NotificationArguments Parse(string? encodedArguments)
    {
        if (string.IsNullOrWhiteSpace(encodedArguments))
            return Empty;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in encodedArguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                values[WebUtility.UrlDecode(pair)] = string.Empty;
                continue;
            }

            var key = WebUtility.UrlDecode(pair[..separatorIndex]);
            var value = WebUtility.UrlDecode(pair[(separatorIndex + 1)..]);

            values[key] = value;
        }

        return new NotificationArguments(values);
    }

    public bool TryGetValue(string key, out string value)
        => _values.TryGetValue(key, out value!);

    public bool TryGetValue<TEnum>(string key, out TEnum value) where TEnum : struct, Enum
    {
        value = default;

        return _values.TryGetValue(key, out var rawValue) &&
               Enum.TryParse(rawValue, ignoreCase: true, out value);
    }
}
