#if DEBUG
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>iPad bridge commands: cal-search TEXT, cal-search-open TEXT N (Mac twins), calcompose-close (closes the composer in place).</summary>
public sealed partial class CalendarPageViewController
{
    protected override void RegisterIdiomDebugCommands()
    {
        UIKitDebugBridge.Register("cal-search", async args =>
        {
            if (Released) return "calendar page released";
            var results = await ViewModel.SearchCalendarItemsAsync(string.Join(' ', args), SearchLimit, CancellationToken.None);
            return results.Count == 0 ? "no results" : string.Join(" | ", results.Select((item, index) => $"{index}:{SearchRow(item).Title} — {SearchRow(item).Subtitle}"));
        });
        UIKitDebugBridge.Register("cal-search-open", async args =>
        {
            if (Released) return "calendar page released";
            int index = args.Length > 1 ? int.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var text = args.Length > 1 ? string.Join(' ', args[..^1]) : (args.Length > 0 ? args[0] : string.Empty);
            var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
            if (index < 0 || index >= results.Count) return $"only {results.Count} results (usage: cal-search-open TEXT N)";
            await Dispatcher.ExecuteOnUIThread(() => OpenSearchResult(results[index]));
            await Task.Delay(1200);
            return $"range={ViewModel.CurrentVisibleRange?.StartDate}..{ViewModel.CurrentVisibleRange?.EndDate} details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}'";
        });
        // The shared hook closes the iPhone sheet; on iPad the composer is in place.
        UIKitDebugBridge.Register("calcompose-close", async _ =>
        {
            if (_composer is null) return "no composer in place";
            await CloseComposerAsync();
            return "ok";
        });
    }

    protected override string DebugLayoutInfo()
        => $"{PadShell?.DescribeColumns()} sidebarCalendars={_sidebar?.CalendarRowCount} composer={_composer is not null} " +
           $"compactRow={_periodCompact} header={(_page?.Header.Hidden == false)} page={GetHashCode():x} left={NavigationItem.LeftBarButtonItems?.Length} leftWindows={string.Join(",", (NavigationItem.LeftBarButtonItems ?? []).Select(item => item.CustomView?.Window is null ? 0 : 1))}";
}
#endif
