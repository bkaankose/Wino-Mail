using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

public class ListItemComparer : IComparer<object>
{
    public bool SortByName { get; set; }

    public int Compare(object x, object y)
    {
        if (x is MailCopy xMail && y is MailCopy yMail)
            return SortByName ? string.Compare(xMail.FromName, yMail.FromName, StringComparison.OrdinalIgnoreCase) : DateTime.Compare(yMail.CreationDate, xMail.CreationDate);
        else if (x is DateTime dateX && y is DateTime dateY)
            return DateTime.Compare(dateY, dateX);
        else if (x is string stringX && y is string stringY)
            return stringY.CompareTo(stringX);

        return 0;
    }
}
