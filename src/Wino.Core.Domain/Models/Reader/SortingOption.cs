using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

public class SortingOption
{
    public SortingOptionType Type { get; set; }
    public string Title { get; set; }

    public SortingOption(string title, SortingOptionType type)
    {
        Title = title;
        Type = type;
    }
}
