using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// Toolbar search across every task list (Windows ToDoPage ITitleBarSearchHost, SearchBarMode.Tasks):
/// typing shows up to six matching tasks under the toolbar field in the shared suggestion list (title,
/// account • list subtitle, completion glyph). ↑/↓ move, Return or a click opens the highlighted task,
/// Return with nothing highlighted opens the first match, Esc closes the list. Opening a task switches to
/// its list and scope and selects it (ToDoPageViewModel.LoadAndSelectTaskAsync). The filter field above
/// the task list still filters the current list only.
/// </summary>
public sealed partial class ToDoPageViewController : IShellSearchTarget, IShellSearchSuggestionTarget
{
    private const int SearchSuggestionLimit = 6;
    private ShellSearchSuggestionList? _searchSuggestions;
    private NSSearchField? _searchField;
    private CancellationTokenSource? _searchCancellation;
    private string _searchText = string.Empty;

    string? IShellSearchTarget.SearchPlaceholder => Translator.ToDoPage_Search;

    public async Task SearchTextChangedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        CancelSearch();
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            _searchSuggestions?.Close();
            return;
        }

        var query = _searchText;
        var cancellation = _searchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cancellation.Token);
            var tasks = await ViewModel.SearchTasksAsync(query, SearchSuggestionLimit, cancellation.Token);
            if (cancellation.IsCancellationRequested || _released || !string.Equals(query, _searchText, StringComparison.Ordinal)) return;
            await Dispatcher.ExecuteOnUIThread(() => ShowSearchSuggestions(tasks));
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchSubmittedAsync(string text)
    {
        _searchText = text ?? string.Empty;
        CancelSearch();
        _searchSuggestions?.Close();
        if (string.IsNullOrWhiteSpace(_searchText)) return;
        var task = (await ViewModel.SearchTasksAsync(_searchText, 1, CancellationToken.None)).FirstOrDefault();
        if (task is not null) await OpenSearchResultAsync(task);
    }

    public Task SearchClearedAsync()
    {
        _searchText = string.Empty;
        CancelSearch();
        _searchSuggestions?.Close();
        return Task.CompletedTask;
    }

    public void SearchFieldEditing(NSSearchField field) => _searchField = field;

    public bool SearchFieldCommand(NSSearchField field, string selector)
    {
        if (_searchSuggestions is not { IsVisible: true } list) return false;
        switch (selector)
        {
            case "moveDown:": return list.Move(1);
            case "moveUp:": return list.Move(-1);
            // Nothing highlighted: the field's own Return submits, which opens the first match.
            case "insertNewline:": return list.TryChoose();
            case "cancelOperation:": list.Close(); return true;
            default: return false;
        }
    }

    public void SearchFieldEndedEditing(NSSearchField field) => _searchSuggestions?.Close();

    private void ShowSearchSuggestions(IReadOnlyList<AccountTask> tasks)
    {
        if (_released || _searchField is not { } field) return;
        if (_searchSuggestions is null)
        {
            _searchSuggestions = new ShellSearchSuggestionList();
            _searchSuggestions.Chosen += SearchSuggestionChosen;
        }
        var suggestions = tasks
            .Select(task => new ShellSearchSuggestion(
                task.Title ?? string.Empty,
                ViewModel.GetTaskSearchSubtitle(task),
                task,
                Image: WinoIcons.Image(task.IsCompleted ? WinoIconGlyph.CheckmarkCircle : WinoIconGlyph.Circle, 16, null,
                    task.IsCompleted ? Translator.ToDoPage_Completed : null)))
            .ToList();
        _searchSuggestions.Show(field, suggestions);
    }

    private void SearchSuggestionChosen(object? sender, ShellSearchSuggestion suggestion)
    {
        if (suggestion.Tag is AccountTask task) Observe(OpenSearchResultAsync(task));
    }

    /// <summary>Opens the task's list and scope, then selects and reveals it (Windows OnTitleBarSearchSubmittedAsync).</summary>
    private async Task OpenSearchResultAsync(AccountTask task)
    {
        var item = await ViewModel.LoadAndSelectTaskAsync(task.Id);
        if (item is null) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released || !_listBound) return;
            // The reload that brought the task in is still queued; apply it now so the row exists.
            RebuildEntries();
            var row = _entries.FindIndex(entry => entry.Item?.Task.Id == task.Id);
            if (row < 0) return;
            _table.SelectRow(row, false);
            _table.ScrollRowToVisible(row);
        });
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    /// <summary>Closes the suggestion list and stops a pending search; called when the page is released.</summary>
    private void ReleaseSearch()
    {
        CancelSearch();
        if (_searchSuggestions is not null)
        {
            _searchSuggestions.Chosen -= SearchSuggestionChosen;
            _searchSuggestions.Dispose();
            _searchSuggestions = null;
        }
        _searchField = null;
    }

#if DEBUG
    private void RegisterSearchDebugCommands()
    {
        // "todo-search TEXT": the suggestion count (and shows the list when the toolbar field is known).
        MacDebugBridge.Register("todo-search", async args =>
        {
            var text = string.Join(' ', args);
            var tasks = await ViewModel.SearchTasksAsync(text, SearchSuggestionLimit, CancellationToken.None);
            await Dispatcher.ExecuteOnUIThread(() => ShowSearchSuggestions(tasks));
            return $"{tasks.Count} suggestions: " + string.Join(" | ", tasks.Select(task => $"{task.Title} ({ViewModel.GetTaskSearchSubtitle(task)})"));
        });
        MacDebugBridge.Register("todo-search-submit", async args =>
        {
            await SearchSubmittedAsync(string.Join(' ', args));
            return $"selected={ViewModel.SelectedTask?.Title} list={ViewModel.SelectedList?.Title}";
        });
    }
#endif
}
