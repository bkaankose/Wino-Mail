#if DEBUG
using Wino.Core.Domain.Entities.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the calendar search: <c>search text</c> (the typed path; underscores are spaces),
/// <c>search-key down|up|enter|escape</c>, <c>search-choose i</c> (clicks a row), <c>search-shot png</c>, <c>search-submit text</c>,
/// <c>search-open n text</c> (opens the n-th result and waits for the details pane).
/// </summary>
internal sealed partial class CalendarPageView
{
    partial void RegisterSearchDebugVerbs()
    {
        Bindings.Own(CalendarDebugActions.AddVerb("search", async args =>
        {
            await SearchTextChangedAsync(string.Join(' ', args).Replace('_', ' '));
            await CalendarPerfProbe.DelayAsync(100);
            return DescribeSearch();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("search-key", args =>
        {
            var taken = args.FirstOrDefault() switch
            {
                "down" => MoveSearchSuggestion(1),
                "up" => MoveSearchSuggestion(-1),
                "enter" => TryChooseSearchSuggestion(),
                "escape" => CloseSearchSuggestions(),
                _ => false
            };
            return $"taken={taken} highlighted='{_searchSuggestions?.Highlighted?.Title}' {DescribeSearch()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("search-shot", args =>
        {
            if (args.Length == 0 || _searchSuggestions is not { IsVisible: true } list) return "usage: search-shot <png> (with the list open)";
            PopoverDebug.Capture(list.DebugPopover, args[0]);
            return "ok";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("search-choose", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var index)) return "usage: search-choose <i>";
            _searchSuggestions?.DebugChoose(index);
            await CalendarPerfProbe.DelayAsync(1500);
            return $"entry='{(_searchAnchor as Gtk.Editable)?.GetText()}' details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}' range='{ViewModel.VisibleDateRangeText}' {DescribeSearch()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("search-submit", async args =>
        {
            await SearchSubmittedAsync(string.Join(' ', args).Replace('_', ' '));
            await CalendarPerfProbe.DelayAsync(1500);
            return $"details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}' range='{ViewModel.VisibleDateRangeText}' {DescribeSearch()}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("search-open", async args =>
        {
            if (args.Length < 2 || !int.TryParse(args[0], out var index)) return "usage: search-open <n> <text>";
            var results = await ViewModel.SearchCalendarItemsAsync(string.Join(' ', args.Skip(1)).Replace('_', ' '), SearchLimit, CancellationToken.None);
            if (index < 0 || index >= results.Count) return $"no result {index} of {results.Count}";
            CalendarItem item = results[index];
            await Dispatcher.ExecuteOnUIThread(() => OpenSearchResult(item));
            for (var wait = 0; wait < 30 && ViewModel.DisplayDetailsCalendarItemViewModel?.CalendarItem.Id != item.Id; wait++)
                await CalendarPerfProbe.DelayAsync(100);
            return $"range='{ViewModel.VisibleDateRangeText}' details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}' start={item.LocalStartDate:yyyy-MM-dd HH:mm}";
        }));
    }

    private string DescribeSearch() => _searchSuggestions?.Describe() ?? "visible=False rows=0";
}
#endif
