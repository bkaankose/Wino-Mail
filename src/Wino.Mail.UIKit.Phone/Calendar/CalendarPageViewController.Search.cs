using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Messaging.Client.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// The iPhone calendar's event search behind the bar's search glyph (approach A of 18 Oct 2026; the iPad toolbar search's twin): after 150 ms
/// the best matches list under the field with "Account • Calendar • date time" and the calendar colour. Choosing one, or
/// submitting text, closes the search and moves the grid to the event's date with the event opened.
/// </summary>
public sealed partial class CalendarPageViewController
{
    private const int SearchLimit = 12;

    private UISearchController? _search;
    private CalendarSearchResultsViewController? _searchResults;
    private SearchHandler? _searchHandler;
    private CancellationTokenSource? _searchCancellation;

    private void BuildSearch()
    {
        var results = new CalendarSearchResultsViewController(OpenSearchResult);
        _searchResults = results;
        _searchHandler = new SearchHandler(this);
        var search = new UISearchController(results)
        {
            ObscuresBackgroundDuringPresentation = true,
            SearchResultsUpdater = _searchHandler
        };
        search.SearchBar.Placeholder = Translator.IOS_Calendar_SearchEvents;
        search.SearchBar.Delegate = _searchHandler;
        _search = search;
        PhoneBarItems.BarSearch(NavigationItem, search);
        DefinesPresentationContext = true;
    }

    private async Task SearchAsync(string text)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text))
        {
            _searchResults?.Show([]);
            return;
        }
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            await Task.Delay(150, token);
            var items = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, token);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (token.IsCancellationRequested || Released) return;
                _searchResults?.Show(items.Select(SearchRow).ToList());
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
    }

    private async Task SubmitSearchAsync(string text)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var items = await ViewModel.SearchCalendarItemsAsync(text, SearchLimit, CancellationToken.None);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!Released && items.FirstOrDefault() is { } first) OpenSearchResult(first);
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>Closes the search and loads the event's range with the event as the pending target (iPad twin).</summary>
    private void OpenSearchResult(CalendarItem item)
    {
        if (_search is { Active: true } search) search.Active = false;
        if (!ViewModel.Readiness.IsReady) return;
        WeakReferenceMessenger.Default.Send(CalendarPageActivation.CreateSearchResultMessage(item, ViewModel.StatePersistanceService.CalendarDisplayType));
    }

    private CalendarSearchRow SearchRow(CalendarItem item)
    {
        var calendar = item.AssignedCalendar;
        var parts = new[] { calendar?.MailAccount?.Name, calendar?.Name, SearchDateText(item.LocalStartDate) }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var title = string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title;
        return new CalendarSearchRow(title, string.Join(" • ", parts), WinoStyle.FromHexString(calendar?.BackgroundColorHex) ?? WinoStyle.Accent, item);
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
        if (_search is { } search)
        {
            search.SearchResultsUpdater = null;
            search.SearchBar.Delegate = null;
        }
    }

    private sealed class SearchHandler(CalendarPageViewController owner) : UISearchResultsUpdating, IUISearchBarDelegate
    {
        public override void UpdateSearchResultsForSearchController(UISearchController searchController)
            => _ = owner.SearchAsync(searchController.SearchBar.Text ?? string.Empty);

        [Export("searchBarSearchButtonClicked:")]
        public void SearchButtonClicked(UISearchBar searchBar) => _ = owner.SubmitSearchAsync(searchBar.Text ?? string.Empty);
    }
}

/// <summary>One calendar search match: the title, "Account • Calendar • date time" and the calendar colour.</summary>
internal sealed record CalendarSearchRow(string Title, string Detail, UIColor Color, CalendarItem Item);

/// <summary>The calendar search's results list under the search field; a tap opens the event.</summary>
internal sealed class CalendarSearchResultsViewController(Action<CalendarItem> open) : UITableViewController(UITableViewStyle.InsetGrouped)
{
    private const string CellIdentifier = "CalendarSearchResult";
    private List<CalendarSearchRow> _rows = [];

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellIdentifier);
        TableView.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.OnDrag;
    }

    public void Show(List<CalendarSearchRow> rows)
    {
        _rows = rows;
        if (IsViewLoaded) TableView.ReloadData();
    }

    public override nint RowsInSection(UITableView tableView, nint section) => _rows.Count;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = tableView.DequeueReusableCell(CellIdentifier, indexPath);
        var row = _rows[indexPath.Row];
        var content = UIListContentConfiguration.SubtitleCellConfiguration;
        content.Text = row.Title;
        content.SecondaryText = row.Detail;
        content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
        content.Image = Dot(row.Color);
        cell.ContentConfiguration = content;
        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Row < _rows.Count) open(_rows[indexPath.Row].Item);
    }

    /// <summary>The calendar colour as a 10pt dot.</summary>
    private static UIImage Dot(UIColor color)
    {
        using var renderer = new UIGraphicsImageRenderer(new CoreGraphics.CGSize(10, 10));
        return renderer.CreateImage(_ =>
        {
            color.SetFill();
            UIBezierPath.FromOval(new CoreGraphics.CGRect(0, 0, 10, 10)).Fill();
        }).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }
}
