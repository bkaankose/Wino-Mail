using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Messaging.Client.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Calendar search from the toolbar field (Windows CalendarPage ITitleBarSearchHost): after 150 ms
/// the six best matches show under the field with "Account • Calendar • date time". Choosing one,
/// or submitting text, moves the grid to the event's date and opens it in the details pane.
/// </summary>
public sealed partial class CalendarPageViewController : IShellSearchTarget, IShellSearchSuggestionTarget
{
    private const int SearchLimit = 6;
    private ShellSearchSuggestionList? _searchSuggestions;
    private CancellationTokenSource? _searchCancellation;
    private NSSearchField? _searchField;

    public void SearchFieldEditing(NSSearchField field) => _searchField = field;

    public bool SearchFieldCommand(NSSearchField field, string selector)
    {
        var list = _searchSuggestions;
        switch (selector)
        {
            case "moveDown:": return list?.Move(1) == true;
            case "moveUp:": return list?.Move(-1) == true;
            // Without a highlighted row the field submits as usual (SearchSubmittedAsync opens the first result).
            case "insertNewline:": return list?.TryChoose() == true;
            case "cancelOperation:":
                if (list?.IsVisible != true) return false;
                list.Close();
                return true;
            default: return false;
        }
    }

    public void SearchFieldEndedEditing(NSSearchField field)
    {
        CancelSearch();
        _searchSuggestions?.Close();
    }

    public async Task SearchTextChangedAsync(string text)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text)) { _searchSuggestions?.Close(); return; }
        var search = new CancellationTokenSource();
        _searchCancellation = search;
        var token = search.Token;
        try
        {
            await Task.Delay(150, token);
            var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, token);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (token.IsCancellationRequested || _released || _searchField is not { } field) return;
                _searchSuggestions ??= CreateSearchSuggestions();
                _searchSuggestions.Show(field, results.Select(SearchRow).ToList());
            });
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchSubmittedAsync(string text)
    {
        CancelSearch();
        // The ViewModel drops inactive calendars after the limit, so ask for as many as the list shows.
        var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _searchSuggestions?.Close();
            if (_released) return;
            if (results.FirstOrDefault() is { } first) OpenSearchResult(first);
            else AppKitFramework.NSBeep();
        });
    }

    public Task SearchClearedAsync()
    {
        CancelSearch();
        _searchSuggestions?.Close();
        return Task.CompletedTask;
    }

    private ShellSearchSuggestionList CreateSearchSuggestions()
    {
        var list = new ShellSearchSuggestionList { Width = 390 };
        list.Chosen += SearchSuggestionChosen;
        return list;
    }

    private void SearchSuggestionChosen(object? sender, ShellSearchSuggestion suggestion)
    {
        if (suggestion.Tag is not CalendarItem item) return;
        CancelSearch();
        if (_searchField is { } field) field.StringValue = suggestion.Title;
        OpenSearchResult(item);
    }

    /// <summary>The pane needs the grid on the event's date: load that range with the event as the pending target.</summary>
    private void OpenSearchResult(CalendarItem item)
    {
        if (!ViewModel.Readiness.IsReady) return;
        var request = new CalendarDisplayRequest(ViewModel.StatePersistanceService.CalendarDisplayType, DateOnly.FromDateTime(item.LocalStartDate));
        WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(request, false, new CalendarItemTarget(item, CalendarEventTargetType.Single)));
    }

    private ShellSearchSuggestion SearchRow(CalendarItem item)
    {
        var calendar = item.AssignedCalendar;
        var parts = new[] { calendar?.MailAccount?.Name, calendar?.Name, SearchDateText(item.LocalStartDate) }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var title = string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title;
        return new ShellSearchSuggestion(title, string.Join(" • ", parts), item, Dot: WinoStyle.FromHexString(calendar?.BackgroundColorHex) ?? WinoStyle.Accent);
    }

    private string SearchDateText(DateTime dateTime)
    {
        var settings = ViewModel.CurrentSettings;
        if (settings is null) return dateTime.ToString("g");
        return $"{dateTime.ToString("d", settings.CultureInfo)} {DateTimeDisplayFormatter.FormatTime(dateTime, settings.DayHeaderDisplayType, settings.CultureInfo)}";
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private void ReleaseSearch()
    {
        CancelSearch();
        _searchField = null;
        if (_searchSuggestions is { } list)
        {
            _searchSuggestions = null;
            list.Chosen -= SearchSuggestionChosen;
            list.Dispose();
        }
    }

#if DEBUG
    /// <summary>"cal-search TEXT" runs the toolbar search path; "cal-search-open N" opens the Nth result.</summary>
    private void RegisterSearchDebugCommands()
    {
        MacDebugBridge.Register("cal-search", async args =>
        {
            var text = string.Join(' ', args);
            var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (_searchField is null) return;
                _searchSuggestions ??= CreateSearchSuggestions();
                _searchSuggestions.Show(_searchField, results.Select(SearchRow).ToList());
            });
            return results.Count == 0 ? "no results" : string.Join(" | ", results.Select((item, index) => $"{index}:{SearchRow(item).Title} — {SearchRow(item).Subtitle}"));
        });
        MacDebugBridge.Register("cal-search-open", async args =>
        {
            int index = args.Length > 1 ? int.Parse(args[^1]) : 0;
            var text = args.Length > 1 ? string.Join(' ', args[..^1]) : (args.Length > 0 ? args[0] : string.Empty);
            var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
            if (index < 0 || index >= results.Count) return $"only {results.Count} results (usage: cal-search-open TEXT N)";
            await Dispatcher.ExecuteOnUIThread(() => OpenSearchResult(results[index]));
            await Task.Delay(1200);
            return $"range={ViewModel.CurrentVisibleRange?.StartDate}..{ViewModel.CurrentVisibleRange?.EndDate} details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}'";
        });
    }
#endif
}
