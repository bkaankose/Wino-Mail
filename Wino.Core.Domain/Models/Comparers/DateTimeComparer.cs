using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Comparers
{
    /// <summary>
    /// Used to insert date grouping into proper place in Reader page.
    /// </summary>
    public class DateTimeComparer : IComparer<DateTime>
    {
        public int Compare(DateTime x, DateTime y)
        {
            return DateTime.Compare(y, x);
        }
    }
}
