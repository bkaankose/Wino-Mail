#if DEBUG
using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>Debug verbs of the page: <c>info</c>, <c>type</c>, <c>prev</c>, <c>next</c>, <c>today</c>, <c>date</c>, <c>calendars</c>, <c>check</c>, <c>due</c>, <c>due-complete i</c>.</summary>
internal sealed partial class CalendarPageView
{
    partial void RegisterDetailsDebugVerbs();
    partial void RegisterComposeDebugVerbs();

    partial void BuildDebug()
    {
        _activated.Add(() =>
        {
            RegisterDetailsDebugVerbs();
            RegisterComposeDebugVerbs();
            Bindings.Own(CalendarDebugActions.AddVerb("info", _ => DebugInfo()));
            Bindings.Own(CalendarDebugActions.AddVerb("type", args =>
            {
                if (args.Length == 0 || !Enum.TryParse<CalendarDisplayType>(args[0], true, out var type)) return "usage: type Day|WorkWeek|Week|Month";
                ViewModel.StatePersistanceService.CalendarDisplayType = type;
                return "ok";
            }));
            Bindings.Own(CalendarDebugActions.AddVerb("prev", _ => { NavigatePeriods(-1); return "ok"; }));
            Bindings.Own(CalendarDebugActions.AddVerb("next", _ => { NavigatePeriods(1); return "ok"; }));
            Bindings.Own(CalendarDebugActions.AddVerb("today", _ => { RequestToday(); return "ok"; }));
            Bindings.Own(CalendarDebugActions.AddVerb("date", args =>
            {
                if (args.Length == 0 || !DateTime.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return "usage: date yyyy-MM-dd";
                var clicked = new CalendarViewDayClickedEventArgs(date);
                if (_shell.DateClickedCommand.CanExecute(clicked)) _shell.DateClickedCommand.Execute(clicked);
                return "ok";
            }));
            Bindings.Own(CalendarDebugActions.AddVerb("calendars", _ => DebugCalendars()));
            Bindings.Own(CalendarDebugActions.AddVerb("due", _ => DebugDescribeDueTasks()));
            Bindings.Own(CalendarDebugActions.AddVerb("due-complete", args => args.Length > 0 && int.TryParse(args[0], out var index) ? DebugCompleteDueTask(index) : "usage: due-complete i"));
            Bindings.Own(CalendarDebugActions.AddVerb("check", args =>
            {
                var calendars = _shell.AccountCalendarStateService.AllCalendars.ToList();
                if (args.Length < 2 || !int.TryParse(args[0], out var index) || index < 0 || index >= calendars.Count) return $"usage: check <0..{calendars.Count - 1}> on|off";
                calendars[index].IsChecked = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                return $"{calendars[index].Name} checked={calendars[index].IsChecked}";
            }));
        });
    }

    private string DebugInfo()
    {
        var range = ViewModel.CurrentVisibleRange;
        var state = _shell.AccountCalendarStateService;
        var visible = range is null ? 0 : ViewModel.CalendarItems.Count(item => item.StartDate < range.EndDate.ToDateTime(TimeOnly.MinValue).AddDays(1) && item.EndDate > range.StartDate.ToDateTime(TimeOnly.MinValue));
        return string.Create(CultureInfo.InvariantCulture,
            $"type={range?.DisplayType} range={range?.StartDate:yyyy-MM-dd}..{range?.EndDate:yyyy-MM-dd} anchor={range?.AnchorDate:yyyy-MM-dd} title='{ViewModel.VisibleDateRangeText}' toolbar='{_toolbar.RangeText}' " +
            $"selected={_toolbar.SelectedType} items={ViewModel.CalendarItems.Count} visibleItems={visible} active={state.ActiveCalendars.Count()} all={state.AllCalendars.Count()} groups={state.GroupedAccountCalendars.Count} " +
            $"ready={ViewModel.Readiness.IsReady} readiness='{ViewModel.Readiness.Title}' {DescribePanes()}");
    }

    private string DebugCalendars()
    {
        var text = new StringBuilder();
        var index = 0;
        foreach (var group in _shell.AccountCalendarStateService.GroupedAccountCalendars)
        {
            text.Append("group ").Append(group.Account?.Name).Append(" state=").Append(group.IsCheckedState?.ToString() ?? "mixed")
                .Append(" expanded=").Append(group.IsExpanded).Append(" colour=").AppendLine(group.AccountColorHex);
            foreach (var calendar in group.AccountCalendars)
                text.Append("  ").Append(index++).Append(' ').Append(calendar.Name).Append(" colour=").Append(calendar.BackgroundColorHex).Append(" checked=").AppendLine(calendar.IsChecked.ToString());
        }
        return text.ToString();
    }
}
#endif
