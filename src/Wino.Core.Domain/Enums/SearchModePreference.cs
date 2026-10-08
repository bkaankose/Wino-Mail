using System;

namespace Wino.Core.Domain.Enums;

public static class SearchModePreference
{
    public static SearchMode Parse(string? stored)
        => Enum.TryParse<SearchMode>(stored, out var mode) && Enum.IsDefined(mode)
            ? mode
            : SearchMode.Local;
}
