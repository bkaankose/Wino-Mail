using System.Collections.Generic;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Comparers;

public class NameComparer : IComparer<IMailItem>
{
    public int Compare(IMailItem x, IMailItem y)
    {
        return string.Compare(x.FromName, y.FromName);
    }
}
