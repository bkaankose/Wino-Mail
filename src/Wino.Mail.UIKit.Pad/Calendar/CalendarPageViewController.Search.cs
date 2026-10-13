using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Shell;
using Wino.Messaging.Client.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>
/// Calendar search from the toolbar field (Windows CalendarPage ITitleBarSearchHost; Mac twin): after 150 ms the six best
/// matches show under the field with "Account • Calendar • date time". Choosing one, or submitting text, moves the grid
/// to the event's date and opens it in the details column.
/// </summary>
public sealed partial class CalendarPageViewController : IShellSearchTarget, IShellSearchSuggestionTarget
{
    private const int SearchLimit = 6;
    private ShellSearchSuggestionController? _searchSuggestions;
    private CancellationTokenSource? _searchCancellation;
    private UISearchTextField? _searchField;

    public void SearchFieldEditing(UISearchTextField field) => _searchField = field;

    public bool SearchFieldCommand(UISearchTextField field, ShellSearchFieldCommand command)
    {
        var list = _searchSuggestions;
        switch (command)
        {
            case ShellSearchFieldCommand.MoveDown: return list?.Move(1) == true;
            case ShellSearchFieldCommand.MoveUp: return list?.Move(-1) == true;
            // Without a highlighted row the field submits as usual (SearchSubmittedAsync opens the first result).
            case ShellSearchFieldCommand.Submit: return list?.TryChoose() == true;
            case ShellSearchFieldCommand.Cancel:
                if (list?.IsVisible != true) return false;
                list.Close();
                return true;
            default: return false;
        }
    }

    public void SearchFieldEndedEditing(UISearchTextField field)
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
                if (token.IsCancellationRequested || Released || _searchField is not { } field) return;
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
            if (Released) return;
            if (results.FirstOrDefault() is { } first) OpenSearchResult(first);
        });
    }

    public Task SearchClearedAsync()
    {
        CancelSearch();
        _searchSuggestions?.Close();
        return Task.CompletedTask;
    }

    private ShellSearchSuggestionController CreateSearchSuggestions()
    {
        var list = new ShellSearchSuggestionController { Width = 390 };
        list.Chosen += SearchSuggestionChosen;
        return list;
    }

    private void SearchSuggestionChosen(object? sender, ShellSearchSuggestion suggestion)
    {
        if (suggestion.Tag is not CalendarItem item) return;
        CancelSearch();
        if (_searchField is { } field) field.Text = suggestion.Title;
        OpenSearchResult(item);
    }

    /// <summary>The details need the grid on the event's date: load that range with the event as the pending target.</summary>
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
}
