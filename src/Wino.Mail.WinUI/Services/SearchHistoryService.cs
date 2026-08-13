using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services;

public sealed class SearchHistoryService(IConfigurationService configurationService) : ISearchHistoryService
{
    private const int MaximumEntries = 8;

    public IReadOnlyList<string> GetHistory(SearchBarMode mode)
    {
        var serialized = configurationService.Get(GetKey(mode), string.Empty);
        if (string.IsNullOrWhiteSpace(serialized))
            return [];

        return serialized.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(Decode)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Take(MaximumEntries)
            .ToArray();
    }

    public void Record(SearchBarMode mode, string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return;

        var entries = GetHistory(mode)
            .Where(existing => !string.Equals(existing, query, StringComparison.OrdinalIgnoreCase))
            .Prepend(query)
            .Take(MaximumEntries)
            .ToArray();
        configurationService.Set(GetKey(mode), string.Join(';', entries.Select(Encode)));
    }

    public void Clear(SearchBarMode mode) => configurationService.Remove(GetKey(mode));

    private static string GetKey(SearchBarMode mode) => $"SearchHistory.{mode}";

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
