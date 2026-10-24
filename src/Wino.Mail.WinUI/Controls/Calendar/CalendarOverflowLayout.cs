using System;
using System.Collections.Generic;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Controls;

internal readonly record struct CalendarOverflowLayout(DateOnly Date, int Column, List<CalendarItemViewModel> HiddenItems, LayoutRect Bounds);
