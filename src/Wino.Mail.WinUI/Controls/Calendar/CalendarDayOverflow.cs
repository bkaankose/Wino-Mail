using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed class CalendarDayOverflow
{
    public CalendarDayOverflow(DateOnly date, List<CalendarItemViewModel> hiddenItems, CultureInfo? culture)
    {
        ArgumentNullException.ThrowIfNull(hiddenItems);

        Date = date;
        HiddenItems = hiddenItems;
        MoreText = CalendarItemOverflow.FormatMoreText(hiddenItems.Count);
        AccessibleName = CalendarItemOverflow.FormatAccessibleName(hiddenItems.Count, date, culture);
    }

    public DateOnly Date { get; }

    public List<CalendarItemViewModel> HiddenItems { get; }

    public string MoreText { get; }

    public string AccessibleName { get; }
}
