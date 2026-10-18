using Android.Content;
using Android.Views;
using Android.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// Calendar search from the docked search bar (Windows CalendarPage title-bar search; iPad and Mac twin): after 150 ms
/// the six best matches drop down under the bar with "Account • Calendar • date time". Choosing one, or submitting the
/// text, moves the calendar to the event's date and opens it.
/// </summary>
public sealed partial class CalendarPageFragment
{
    private const int SearchLimit = 6;

    private DockedSearchBar? _searchBar;
    private ListPopupWindow? _searchResults;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<CalendarItem> _searchItems = [];

    private void BuildSearch(Context context)
    {
        if (_toolbar is null) return;
        _searchBar = DockedSearchBar.Attach(_toolbar, this, Translator.Android_Calendar_SearchPlaceholder, () => Chrome?.ToggleNavigation());
        _searchBar.QueryChanged += text => _ = SearchChangedAsync(text);
        _searchBar.Submitted += text => _ = SearchSubmittedAsync(text);
        _searchBar.ActiveChanged += active => { if (!active) CloseSearchResults(); };
        Bindings.Own(new ActionDisposable(() =>
        {
            CancelSearch();
            CloseSearchResults();
        }));
    }

    private async Task SearchChangedAsync(string text)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text) || ViewModel is not { } viewModel)
        {
            CloseSearchResults();
            return;
        }
        var search = _searchCancellation = new CancellationTokenSource();
        var token = search.Token;
        try
        {
            await Task.Delay(150, token);
            var results = await viewModel.SearchCalendarItemsAsync(text, SearchLimit, token);
            if (token.IsCancellationRequested || Entry?.Dispatcher is not { } dispatcher) return;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (!token.IsCancellationRequested && View is not null) ShowSearchResults(results);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
    }

    private async Task SearchSubmittedAsync(string text)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text) || ViewModel is not { } viewModel) return;
        try
        {
            var results = await viewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
            if (Entry?.Dispatcher is not { } dispatcher) return;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (results.FirstOrDefault() is { } first) OpenSearchResult(first);
                else CloseSearchResults();
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ShowSearchResults(IReadOnlyList<CalendarItem> results)
    {
        if (_toolbar?.Context is not { } context) return;
        if (results.Count == 0)
        {
            CloseSearchResults();
            return;
        }
        _searchItems = results;
        var popup = _searchResults ??= CreateSearchResults(context);
        popup.SetAdapter(new SearchResultAdapter(context, results, SearchDetail));
        popup.Width = _toolbar.Width;
        popup.Show();
    }

    private ListPopupWindow CreateSearchResults(Context context)
    {
        var popup = new ListPopupWindow(context)
        {
            AnchorView = _toolbar,
            // Typing continues in the bar while the results show.
            InputMethodMode = ListPopupWindowInputMethodMode.Needed,
            Modal = false,
            VerticalOffset = WinoUnits.Dp(context, 4)
        };
        var background = new global::Android.Graphics.Drawables.GradientDrawable();
        background.SetCornerRadius(WinoUnits.DpF(context, 16));
        background.SetColor(WinoColors.ZoneFill(context));
        popup.SetBackgroundDrawable(background);
        popup.ItemClick += (_, args) =>
        {
            if (args.Position >= 0 && args.Position < _searchItems.Count) OpenSearchResult(_searchItems[args.Position]);
        };
        return popup;
    }

    /// <summary>The calendar moves to the event's date with the event as the pending details target.</summary>
    private void OpenSearchResult(CalendarItem item)
    {
        CloseSearchResults();
        _searchBar?.Deactivate();
        if (ViewModel is not { } viewModel || !viewModel.Readiness.IsReady) return;
        WeakReferenceMessenger.Default.Send(CalendarPageActivation.CreateSearchResultMessage(item, viewModel.StatePersistanceService.CalendarDisplayType));
    }

    private string SearchDetail(CalendarItem item)
    {
        var calendar = item.AssignedCalendar;
        var settings = ViewModel?.CurrentSettings;
        var when = settings is null
            ? item.LocalStartDate.ToString("g")
            : $"{item.LocalStartDate.ToString("d", settings.CultureInfo)} {DateTimeDisplayFormatter.FormatTime(item.LocalStartDate, settings.DayHeaderDisplayType, settings.CultureInfo)}";
        return string.Join(" • ", new[] { calendar?.MailAccount?.Name, calendar?.Name, when }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private void CloseSearchResults()
    {
        if (_searchResults is { IsShowing: true } popup) popup.Dismiss();
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    /// <summary>A result row: the event title and its account, calendar and start.</summary>
    private sealed class SearchResultAdapter(Context context, IReadOnlyList<CalendarItem> items, Func<CalendarItem, string> detail) : BaseAdapter
    {
        public override int Count => items.Count;
        public override Java.Lang.Object? GetItem(int position) => null;
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var item = items[position];
            var row = new LinearLayout(context) { Orientation = Orientation.Vertical };
            int horizontal = WinoUnits.Dp(context, 16), vertical = WinoUnits.Dp(context, 10);
            row.SetPadding(horizontal, vertical, horizontal, vertical);
            row.SetMinimumHeight(WinoUnits.Dp(context, 56));
            var title = WinoTokens.Style(new TextView(context) { Text = string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title }, WinoTextStyle.BodyStrong);
            title.SetTextColor(WinoColors.PrimaryText(context));
            title.SetSingleLine(true);
            title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            var subtitle = WinoTokens.Style(new TextView(context) { Text = detail(item) }, WinoTextStyle.Caption);
            subtitle.SetTextColor(WinoColors.SecondaryText(context));
            subtitle.SetSingleLine(true);
            subtitle.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            row.AddView(title);
            row.AddView(subtitle);
            row.ContentDescription = $"{title.Text}, {subtitle.Text}";
            return row;
        }
    }
}
