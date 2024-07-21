using Wino.Domain.Enums;
using Wino.Domain.Models.Comparers;
using Wino.Domain.Models.MailItem;

namespace Wino.Domain.Models.Reader
{
    public class SortingOption
    {
        public SortingOptionType Type { get; set; }
        public string Title { get; set; }
        public IComparer<IMailItem> Comparer
        {
            get
            {
                if (Type == SortingOptionType.ReceiveDate)
                    return new DateComparer();
                else
                    return new NameComparer();
            }
        }

        public SortingOption(string title, SortingOptionType type)
        {
            Title = title;
            Type = type;
        }
    }
}
