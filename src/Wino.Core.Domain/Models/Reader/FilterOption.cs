using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

public class FilterOption
{
    public FilterOptionType Type { get; set; }
    public string Title { get; set; }

    public FilterOption(string title, FilterOptionType type)
    {
        Title = title;
        Type = type;
    }
}
