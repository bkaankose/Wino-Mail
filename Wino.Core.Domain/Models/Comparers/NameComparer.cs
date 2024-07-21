using System.Collections.Generic;
using Wino.Domain.Models.MailItem;

namespace Wino.Domain.Models.Comparers
{
    public class NameComparer : IComparer<IMailItem>
    {
        public int Compare(IMailItem x, IMailItem y)
        {
            return string.Compare(x.FromName, y.FromName);
        }
    }
}
