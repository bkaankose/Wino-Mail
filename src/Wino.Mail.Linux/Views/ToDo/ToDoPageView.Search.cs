using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// Header search across every task list (Windows ITitleBarSearchHost in Tasks mode, macOS ToDoPageViewController.Search):
/// typing lists up to six matching tasks in the shell's suggestion list under the header entry (the calendar's too),
/// which never takes the keyboard focus; Up/Down move through it, a chosen row or Return opens the task's list and scope
/// and selects it. The suggestions popover (My Day) lives here too.
/// </summary>
internal sealed partial class ToDoPageView : IShellSearchTarget, IShellSearchSuggestionTarget
{
    private const int SearchResultLimit = 6;

    private CancellationTokenSource? _search;
    private string _searchText = string.Empty;
    private string? _searchEcho;
    private SearchSuggestionList? _searchSuggestions;
    private Gtk.Widget? _searchAnchor;
    private bool _searchReleaseOwned;
    private Gtk.Popover? _suggestionsPopover;
    private Gtk.Box? _suggestionsList;
    private readonly List<IDisposable> _suggestionRows = [];

    string? IShellSearchTarget.SearchPlaceholder => Translator.ToDoPage_Search;

    internal AccountTask[] SearchResults { get; private set; } = [];

    internal bool IsSuggestionsShown => _suggestionsPopover?.GetVisible() == true;

    public async Task SearchTextChangedAsync(string text)
    {
        // The entry shows a chosen task's title. GTK reports the cleared field at once and the title after its delay.
        if (_searchEcho is not null && string.IsNullOrEmpty(text)) return;
        if (text == _searchEcho)
        {
            _searchEcho = null;
            return;
        }
        _searchEcho = null;
        _searchText = text ?? string.Empty;
        var token = RestartSearch();
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            ShowSearchResults([]);
            return;
        }
        var query = _searchText;
        try
        {
            await Task.Delay(150, token);
            var tasks = await ViewModel.SearchTasksAsync(query, SearchResultLimit, token);
            if (token.IsCancellationRequested || IsReleased || !string.Equals(query, _searchText, StringComparison.Ordinal)) return;
            AccountTask[] rows = [.. tasks.Where(static task => task is not null)];
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!token.IsCancellationRequested && !IsReleased) ShowSearchResults(rows);
            });
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchSubmittedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        RestartSearch();
        await Dispatcher.ExecuteOnUIThread(() => ShowSearchResults([]));
        if (string.IsNullOrWhiteSpace(_searchText) || IsReleased) return;
        var task = (await ViewModel.SearchTasksAsync(_searchText, 1, CancellationToken.None)).FirstOrDefault();
        if (task is not null) await OpenSearchResultAsync(task);
        else await Dispatcher.ExecuteOnUIThread(() => Root.ErrorBell());
    }

    public Task SearchClearedAsync()
    {
        _searchText = string.Empty;
        RestartSearch();
        return Dispatcher.ExecuteOnUIThread(() => ShowSearchResults([]));
    }

    public void SetSearchAnchor(Gtk.Widget? entry)
    {
        if (ReferenceEquals(_searchAnchor, entry)) return;
        RestartSearch();
        ReleaseSearchSuggestions();
        _searchAnchor = entry;
        if (_searchReleaseOwned || IsReleased) return;
        _searchReleaseOwned = true;
        Bindings.Own(new ActionDisposable(() =>
        {
            _search?.Cancel();
            ReleaseSearchSuggestions();
            _searchAnchor = null;
        }));
    }

    public bool MoveSearchSuggestion(int delta) => _searchSuggestions?.Move(delta) == true;

    // Without a highlighted row the entry submits as usual and SearchSubmittedAsync opens the first result.
    public bool TryChooseSearchSuggestion() => _searchSuggestions?.TryChoose() == true;

    public bool CloseSearchSuggestions()
    {
        if (_searchSuggestions is not { IsVisible: true } list) return false;
        RestartSearch();
        list.Close();
        return true;
    }

    public void SearchFocusLeft()
    {
        RestartSearch();
        _searchSuggestions?.Close();
    }

    /// <summary>Opens the task's list and scope, then selects and reveals it (Windows OnTitleBarSearchSubmittedAsync).</summary>
    internal async Task OpenSearchResultAsync(AccountTask task)
    {
        var item = await ViewModel.LoadAndSelectTaskAsync(task.Id);
        if (item is null || IsReleased) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased) RevealTask(task.Id);
        });
    }

#if DEBUG
    internal string DescribeSearchSuggestions() => _searchSuggestions?.Describe() ?? "visible=False rows=0";

    internal void ChooseSearchSuggestion(int index) => _searchSuggestions?.DebugChoose(index);

    internal Gtk.Popover? SearchSuggestionPopover => _searchSuggestions is { IsVisible: true } list ? list.DebugPopover : null;
#endif

    private CancellationToken RestartSearch()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = new CancellationTokenSource();
        return _search.Token;
    }

    private void ShowSearchResults(AccountTask[] tasks)
    {
        if (IsReleased) return;
        SearchResults = tasks;
        if (tasks.Length == 0 || _searchAnchor is not { } anchor)
        {
            _searchSuggestions?.Close();
            return;
        }
        _searchSuggestions ??= CreateSearchSuggestions();
        _searchSuggestions.Show(anchor, [.. tasks.Select(SearchRow)]);
    }

    private SearchSuggestionList CreateSearchSuggestions()
    {
        var list = new SearchSuggestionList(390);
        list.Chosen += SearchSuggestionChosen;
        return list;
    }

    private void ReleaseSearchSuggestions()
    {
        if (_searchSuggestions is not { } list) return;
        _searchSuggestions = null;
        list.Chosen -= SearchSuggestionChosen;
        list.Dispose();
    }

    private void SearchSuggestionChosen(SearchSuggestion suggestion)
    {
        if (suggestion.Tag is not AccountTask task) return;
        RestartSearch();
        _searchSuggestions?.Close();
        if (_searchAnchor is Gtk.Editable editable && editable.GetText() != suggestion.Title)
        {
            _searchEcho = suggestion.Title;
            editable.SetText(suggestion.Title);
        }
        Observe(OpenSearchResultAsync(task));
    }

    private SearchSuggestion SearchRow(AccountTask task)
    {
        var icon = WinoIcons.Label(task.IsCompleted ? WinoIconGlyph.CheckmarkCircle : WinoIconGlyph.Circle, 16);
        icon.SetValign(Gtk.Align.Center);
        var title = string.IsNullOrWhiteSpace(task.Title) ? Translator.CalDavTasks_UntitledTask : task.Title;
        return new SearchSuggestion(title, ViewModel.GetTaskSearchSubtitle(task), task, Leading: icon);
    }

    private static Gtk.Button ResultButton(WinoIconGlyph glyph, string title, string? subtitle)
    {
        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.SetCanFocus(false);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        var icon = WinoIcons.Label(glyph, 16);
        icon.SetValign(Gtk.Align.Center);
        row.Append(icon);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        var name = Gtk.Label.New(title);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.SetMaxWidthChars(40);
        text.Append(name);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var caption = Gtk.Label.New(subtitle);
            caption.AddCssClass("caption");
            caption.AddCssClass("dim-label");
            caption.SetXalign(0);
            caption.SetEllipsize(Pango.EllipsizeMode.End);
            caption.SetMaxWidthChars(40);
            text.Append(caption);
        }
        row.Append(text);
        button.SetChild(row);
        WinoA11y.Rename(button, string.IsNullOrEmpty(subtitle) ? title : $"{title}, {subtitle}");
        return button;
    }

    private void BindSuggestions()
    {
        Bind.Bind(nameof(ToDoPageViewModel.IsSuggestionsOpen), static vm => vm.IsSuggestionsOpen, open =>
        {
            if (open) PresentSuggestions();
            else _suggestionsPopover?.Popdown();
        });
        Bind.BindCollection(ViewModel.Suggestions, () =>
        {
            if (_suggestionsPopover?.GetVisible() == true) FillSuggestions();
        });
    }

    internal void ShowSuggestions()
    {
        if (IsReleased) return;
        if (ViewModel.IsSuggestionsOpen) PresentSuggestions();
        else ViewModel.OpenSuggestionsCommand.Execute(null);
    }

    private void PresentSuggestions()
    {
        if (IsReleased) return;
        if (_suggestionsPopover is null)
        {
            var self = _self;
            var column = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            column.SetMarginTop(4);
            column.SetMarginBottom(4);
            column.SetMarginStart(4);
            column.SetMarginEnd(4);
            var title = Gtk.Label.New(Translator.ToDoPage_Suggestions);
            title.AddCssClass("heading");
            title.SetXalign(0);
            column.Append(title);
            _suggestionsList = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroller.SetPropagateNaturalHeight(true);
            scroller.SetMaxContentHeight(420);
            scroller.SetSizeRequest(320, -1);
            scroller.SetChild(_suggestionsList);
            column.Append(scroller);
            var popover = _suggestionsPopover = Gtk.Popover.New();
            popover.SetChild(column);
            popover.SetPosition(Gtk.PositionType.Bottom);
            popover.SetParent(_header.SuggestionsAnchor);
            Bindings.Own(GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) =>
            {
                if (self.Value is { } view && view.ViewModel.IsSuggestionsOpen) view.ViewModel.CloseSuggestionsCommand.Execute(null);
            }));
            Bindings.Own(new ActionDisposable(() =>
            {
                foreach (var row in _suggestionRows) row.Dispose();
                _suggestionRows.Clear();
                popover.Popdown();
                popover.Unparent();
            }));
        }
        FillSuggestions();
        _suggestionsPopover.Popup();
    }

    private void FillSuggestions()
    {
        if (_suggestionsList is null) return;
        foreach (var row in _suggestionRows) row.Dispose();
        _suggestionRows.Clear();
        while (_suggestionsList.GetFirstChild() is { } child) _suggestionsList.Remove(child);
        TaskItemViewModel[] suggestions = [.. ViewModel.Suggestions.Where(static item => item is not null).Take(12)];
        if (suggestions.Length == 0)
        {
            var empty = Gtk.Label.New(Translator.ToDoPage_SuggestionsEmpty);
            empty.AddCssClass("dim-label");
            empty.SetWrap(true);
            empty.SetXalign(0);
            _suggestionsList.Append(empty);
            return;
        }
        var self = _self;
        foreach (var suggestion in suggestions)
        {
            var button = ResultButton(WinoIconGlyph.Add, suggestion.Title ?? string.Empty, SuggestionCaption(suggestion));
            button.SetCanFocus(true);
            var chosen = suggestion;
            _suggestionRows.Add(GtkSignals.OnClicked(button, () =>
            {
                if (self.Value is not { } view) return;
                view.Observe(view.ViewModel.AddSuggestionToMyDayCommand.ExecuteAsync(chosen));
                view._suggestionsPopover?.Popdown();
            }));
            _suggestionsList.Append(button);
        }
    }

    private static string SuggestionCaption(TaskItemViewModel item)
        => string.Join(" · ", new[] { item.ListName, item.HasDueDate ? item.DueDisplayText : null }.Where(static part => !string.IsNullOrEmpty(part)));
}
