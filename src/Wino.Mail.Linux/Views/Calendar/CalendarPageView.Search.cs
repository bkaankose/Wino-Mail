using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Calendar search from the header entry (Windows CalendarPage ITitleBarSearchHost, macOS CalendarPageViewController.Search):
/// the six best matches show under the entry with "Account • Calendar • date time". Choosing one, or submitting text,
/// moves the grid to the event's date and opens it in the details pane. The header already debounces typing.
/// </summary>
internal sealed partial class CalendarPageView : IShellSearchTarget, IShellSearchSuggestionTarget
{
    private const int SearchLimit = 6;
    private SearchSuggestionList? _searchSuggestions;
    private CancellationTokenSource? _searchCancellation;
    private Gtk.Widget? _searchAnchor;
    private string? _searchEcho;

    partial void RegisterSearchDebugVerbs();

    partial void BuildSearch()
    {
        _activated.Add(() => RegisterSearchDebugVerbs());
        _deactivating.Add(() =>
        {
            ReleaseSearch();
            return Task.CompletedTask;
        });
    }

    public async Task SearchTextChangedAsync(string text)
    {
        // The entry shows a chosen event's title. GTK reports the cleared field at once and the title after its delay.
        if (_searchEcho is not null && string.IsNullOrEmpty(text)) return;
        if (text == _searchEcho)
        {
            _searchEcho = null;
            return;
        }
        _searchEcho = null;
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text))
        {
            _searchSuggestions?.Close();
            return;
        }
        var search = _searchCancellation = new CancellationTokenSource();
        var token = search.Token;
        try
        {
            var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, token);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (token.IsCancellationRequested || IsReleased || _searchAnchor is not { } anchor) return;
                _searchSuggestions ??= CreateSearchSuggestions();
                _searchSuggestions.Show(anchor, [.. results.Select(SearchRow)]);
            });
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchSubmittedAsync(string text)
    {
        CancelSearch();
        // The ViewModel drops inactive calendars after the limit, so it is asked for as many as the list shows.
        var results = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _searchSuggestions?.Close();
            if (IsReleased) return;
            if (results.FirstOrDefault() is { } first) OpenSearchResult(first);
            else Root.ErrorBell();
        });
    }

    public Task SearchClearedAsync()
    {
        CancelSearch();
        _searchSuggestions?.Close();
        return Task.CompletedTask;
    }

    public void SetSearchAnchor(Gtk.Widget? entry)
    {
        if (ReferenceEquals(_searchAnchor, entry)) return;
        CancelSearch();
        _searchSuggestions?.Dispose();
        _searchSuggestions = null;
        _searchAnchor = entry;
    }

    public bool MoveSearchSuggestion(int delta) => _searchSuggestions?.Move(delta) == true;

    // Without a highlighted row the entry submits as usual and SearchSubmittedAsync opens the first result.
    public bool TryChooseSearchSuggestion() => _searchSuggestions?.TryChoose() == true;

    public bool CloseSearchSuggestions()
    {
        if (_searchSuggestions is not { IsVisible: true } list) return false;
        CancelSearch();
        list.Close();
        return true;
    }

    public void SearchFocusLeft()
    {
        CancelSearch();
        _searchSuggestions?.Close();
    }

    private SearchSuggestionList CreateSearchSuggestions()
    {
        var list = new SearchSuggestionList(390);
        list.Chosen += SearchSuggestionChosen;
        return list;
    }

    private void SearchSuggestionChosen(SearchSuggestion suggestion)
    {
        if (suggestion.Tag is not CalendarItem item) return;
        CancelSearch();
        if (_searchAnchor is Gtk.Editable editable && editable.GetText() != suggestion.Title)
        {
            _searchEcho = suggestion.Title;
            editable.SetText(suggestion.Title);
        }
        OpenSearchResult(item);
    }

    /// <summary>The pane needs the grid on the event's date: the range is loaded with the event as the pending target.</summary>
    private void OpenSearchResult(CalendarItem item)
    {
        if (!ViewModel.Readiness.IsReady) return;
        Messenger.Send(CalendarPageActivation.CreateSearchResultMessage(item, ViewModel.StatePersistanceService.CalendarDisplayType));
    }

    private SearchSuggestion SearchRow(CalendarItem item)
    {
        var calendar = item.AssignedCalendar;
        var parts = new[] { calendar?.MailAccount?.Name, calendar?.Name, SearchDateText(item.LocalStartDate) }
            .Where(static part => !string.IsNullOrWhiteSpace(part));
        var title = string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title;
        return new SearchSuggestion(title, string.Join(" • ", parts), item, DotColorHex: calendar?.BackgroundColorHex ?? string.Empty);
    }

    private string SearchDateText(DateTime dateTime)
    {
        if (ViewModel.CurrentSettings is not { } settings) return dateTime.ToString("g");
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
        if (_searchSuggestions is { } list)
        {
            _searchSuggestions = null;
            list.Chosen -= SearchSuggestionChosen;
            list.Dispose();
        }
        _searchAnchor = null;
    }
}
