using System;
using System.Collections;
using System.Collections.Generic;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Comparers;

public class DateComparer : IComparer<IMailItem>, IEqualityComparer
{
    public int Compare(IMailItem x, IMailItem y)
    {
        return DateTime.Compare(y.CreationDate, x.CreationDate);
    }

    public new bool Equals(object x, object y)
    {
        if (x is IMailItem firstItem && y is IMailItem secondItem)
        {
            return firstItem.Equals(secondItem);
        }

        return false;
    }

    public int GetHashCode(object obj) => (obj as IMailItem).GetHashCode();

    public DateComparer()
    {

    }
}
