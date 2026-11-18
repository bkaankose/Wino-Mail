using System;
using System.Collections.Generic;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.WinUI.Controls.Calendar;

internal readonly record struct CalendarOverflowLayout(DateOnly Date, int Column, List<CalendarItemViewModel> HiddenItems, LayoutRect Bounds);
