using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Comparers;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Reader
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
