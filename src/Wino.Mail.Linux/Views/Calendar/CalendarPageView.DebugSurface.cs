#if DEBUG
using System.Globalization;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>Debug verbs of the surface: <c>tiles</c>, <c>scroll [hour]</c>, <c>month</c>, <c>bars</c>.</summary>
internal sealed partial class CalendarPageView
{
    private void RegisterSurfaceDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("tiles", _ => surface.DescribeTiles()));
        Bindings.Own(CalendarDebugActions.AddVerb("month", _ => surface.DescribeMonth()));
        Bindings.Own(CalendarDebugActions.AddVerb("bars", _ => surface.DescribeBars()));
        Bindings.Own(CalendarDebugActions.AddVerb("scroll", args =>
        {
            var adjustment = surface.VerticalAdjustment;
            if (args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hour))
                adjustment.SetValue(WinoCalendarSurfaceGeometry.TimelinePadding + hour * WinoCalendarSurfaceGeometry.HourHeight(ViewModel.CurrentSettings?.HourHeight ?? 0));
            var startHour = ViewModel.CurrentSettings?.IsWorkingHoursEnabled == true ? ViewModel.CurrentSettings.WorkingHourStart.TotalHours : 8;
            var hourHeight = WinoCalendarSurfaceGeometry.HourHeight(ViewModel.CurrentSettings?.HourHeight ?? 0);
            var expected = WinoCalendarSurfaceGeometry.InitialScrollOffset(startHour, hourHeight, WinoCalendarSurfaceGeometry.CanvasHeight(hourHeight), adjustment.GetPageSize());
            return string.Create(CultureInfo.InvariantCulture, $"value={adjustment.GetValue():0.#} upper={adjustment.GetUpper():0.#} page={adjustment.GetPageSize():0.#} initialExpected={expected:0.#}");
        }));
    }
}
#endif
