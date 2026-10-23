using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Reader;

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
