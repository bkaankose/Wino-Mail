using System;
using System.Collections.Generic;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Comparers;

public class ListItemComparer : IComparer<object>
{
    public bool SortByName { get; set; }

    public DateComparer DateComparer = new DateComparer();
    public readonly NameComparer NameComparer = new NameComparer();

    public int Compare(object x, object y)
    {
        if (x is IMailItem xMail && y is IMailItem yMail)
        {
            var itemComparer = GetItemComparer();

            return itemComparer.Compare(xMail, yMail);
        }
        else if (x is DateTime dateX && y is DateTime dateY)
            return DateTime.Compare(dateY, dateX);
        else if (x is string stringX && y is string stringY)
            return stringY.CompareTo(stringX);

        return 0;
    }

    public IComparer<IMailItem> GetItemComparer()
    {
        if (SortByName)
            return NameComparer;
        else
            return DateComparer;
    }
}
