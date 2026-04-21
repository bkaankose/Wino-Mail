using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

public class ListItemComparer : IComparer<object>
{
    public bool SortByName { get; set; }

    public int Compare(object x, object y)
    {
        if (x is MailListGroupKey xGroupKey && y is MailListGroupKey yGroupKey)
        {
            if (xGroupKey.IsPinned != yGroupKey.IsPinned)
                return yGroupKey.IsPinned.CompareTo(xGroupKey.IsPinned);

            if (xGroupKey.IsPinned && yGroupKey.IsPinned)
                return 0;

            return CompareSortValues(xGroupKey.Value, yGroupKey.Value);
        }

        if (x is IMailListItemSorting xSorting && y is IMailListItemSorting ySorting)
        {
            if (xSorting.IsPinned != ySorting.IsPinned)
                return ySorting.IsPinned.CompareTo(xSorting.IsPinned);

            return SortByName
                ? string.Compare(xSorting.SortingName, ySorting.SortingName, StringComparison.OrdinalIgnoreCase)
                : DateTime.Compare(ySorting.SortingDate, xSorting.SortingDate);
        }

        if (x is MailCopy xMail && y is MailCopy yMail)
        {
            if (xMail.IsPinned != yMail.IsPinned)
                return yMail.IsPinned.CompareTo(xMail.IsPinned);

            return SortByName
                ? string.Compare(xMail.FromName, yMail.FromName, StringComparison.OrdinalIgnoreCase)
                : DateTime.Compare(yMail.CreationDate, xMail.CreationDate);
        }

        return CompareSortValues(x, y);
    }

    private static int CompareSortValues(object x, object y)
    {
        if (x is DateTime dateX && y is DateTime dateY)
            return DateTime.Compare(dateY, dateX);

        if (x is string stringX && y is string stringY)
            return stringY.CompareTo(stringX);

        return 0;
    }
}
