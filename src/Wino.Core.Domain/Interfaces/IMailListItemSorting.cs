using System;

namespace Wino.Core.Domain.Interfaces;

public interface IMailListItemSorting
{
    DateTime SortingDate { get; }
    string SortingName { get; }
    bool IsPinned { get; }
}
