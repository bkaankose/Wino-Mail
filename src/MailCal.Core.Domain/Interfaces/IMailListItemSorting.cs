using System;

namespace MailCal.Core.Domain.Interfaces;

public interface IMailListItemSorting
{
    DateTime SortingDate { get; }
    string SortingName { get; }
    bool IsPinned { get; }
}
