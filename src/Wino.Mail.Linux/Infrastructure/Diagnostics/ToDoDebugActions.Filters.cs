#if DEBUG
using System.Globalization;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Filter, menu and search verbs of <c>debug-todo</c>: filter text (types into the filter entry), scope
/// active|completed|all, important on|off, sort kind, clear-filters, filter-state, filter-menu scope|sort|compact (opens
/// the chip's menu and describes it), suggestions, suggest-add i, task-menu i (opens the row menu and describes it),
/// task-invoke i path, pickdate-show i, pickdate i yyyy-MM-dd, search text, search-open i, submit text.
/// </summary>
internal static partial class ToDoDebugActions
{
    static partial void AddFilterVerbs()
    {
        Verb("filter", args =>
        {
            var page = Page;
            page.FilterEntry?.SetText(string.Join(' ', args));
            return "typed";
        });
        Verb("scope", args =>
        {
            Page.ViewModel.SetCompletionScopeCommand.Execute(args.Length > 0 ? args[0] : "active");
            return FilterState(Page);
        });
        Verb("important", args =>
        {
            var page = Page;
            var on = args.Length > 0 && args[0] == "on";
            if (page.ViewModel.IsImportantTasksFilterSelected != on) page.ToggleImportantFilter();
            return FilterState(page);
        });
        Verb("sort", args =>
        {
            Page.ViewModel.SetSortCommand.Execute(args.Length > 0 ? args[0] : "due");
            return FilterState(Page);
        });
        Verb("clear-filters", _ =>
        {
            Page.ViewModel.ClearFiltersCommand.Execute(null);
            return FilterState(Page);
        });
        Verb("filter-state", _ => FilterState(Page));
        Verb("filter-menu", args =>
        {
            var page = Page;
            var menu = WinoMenus.Create();
            switch (args.Length > 0 ? args[0] : "compact")
            {
                case "scope": ToDoPageView.ScopeItems(page, menu); break;
                case "sort": ToDoPageView.SortItems(page, menu); break;
                default: ToDoPageView.CompactItems(page, menu); break;
            }
            return ContextMenu.Show(page.Header.Root, menu.Build()).Describe();
        });
        Verb("suggestions", _ =>
        {
            var page = Page;
            page.ShowSuggestions();
            return $"open={page.ViewModel.IsSuggestionsOpen} shown={page.IsSuggestionsShown} | "
                + string.Join(" | ", page.ViewModel.Suggestions.Select(static item => item?.Title));
        });
        Verb("suggest-add", async args =>
        {
            var page = Page;
            var index = (int)Index(args, 0);
            if (index >= page.ViewModel.Suggestions.Count) return "no suggestion";
            var item = page.ViewModel.Suggestions[index];
            await page.ViewModel.AddSuggestionToMyDayCommand.ExecuteAsync(item);
            return "added " + item?.Title;
        });
        Verb("task-menu", args =>
        {
            var page = Page;
            var item = page.Sections.ItemAt(Index(args, 0)) ?? throw new ArgumentException("no row");
            var menu = WinoMenus.Create();
            page.PopulateTaskMenu(menu, item, page.Cells.CellOf(item));
            var anchor = page.Cells.CellOf(item)?.Root ?? page.List;
            return ContextMenu.Show(anchor, menu.Build()).Describe();
        });
        Verb("task-invoke", args =>
        {
            if (args.Length < 2) throw new ArgumentException("task-invoke needs a row and a path");
            var page = Page;
            var item = page.Sections.ItemAt(Index(args, 0)) ?? throw new ArgumentException("no row");
            var menu = WinoMenus.Create();
            page.PopulateTaskMenu(menu, item, page.Cells.CellOf(item));
            var popover = ContextMenu.Show(page.Cells.CellOf(item)?.Root ?? page.List, menu.Build());
            var path = string.Join(' ', args[1..]);
            return popover.Activate(path) ? "ran " + path : "no entry " + path;
        });
        Verb("pickdate-show", args =>
        {
            var page = Page;
            var item = page.Sections.ItemAt(Index(args, 0)) ?? throw new ArgumentException("no row");
            page.ShowDueDatePicker(item);
            return "shown=" + (page.DatePicker?.Popover.GetVisible() ?? false);
        });
        Verb("pickdate", args =>
        {
            var page = Page;
            var item = page.Sections.ItemAt(Index(args, 0)) ?? throw new ArgumentException("no row");
            var date = DateOnly.ParseExact(args.Length > 1 ? args[1] : string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            page.ShowDueDatePicker(item);
            if (page.DatePicker?.Calendar.DayButton(date) is not { } day) return "no day button";
            day.Activate();
            return "picked " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        });
        Verb("search", async args =>
        {
            var page = Page;
            await page.SearchTextChangedAsync(string.Join(' ', args));
            return string.Join(" | ", page.SearchResults.Select(task => $"{task.Title} ({page.ViewModel.GetTaskSearchSubtitle(task)})"));
        });
        Verb("search-list", _ => Page.DescribeSearchSuggestions());
        Verb("search-shot", args =>
        {
            if (args.Length == 0 || Page.SearchSuggestionPopover is not { } popover) return "usage: search-shot <png> (with the list open)";
            PopoverDebug.Capture(popover, args[0]);
            return "captured";
        });
        Verb("search-choose", async args =>
        {
            var page = Page;
            page.ChooseSearchSuggestion((int)Index(args, 0));
            await Task.Delay(600);
            return Selected(page);
        });
        Verb("search-key", args =>
        {
            IShellSearchSuggestionTarget page = Page;
            var taken = (args.Length > 0 ? args[0] : string.Empty) switch
            {
                "down" => page.MoveSearchSuggestion(1),
                "up" => page.MoveSearchSuggestion(-1),
                "enter" => page.TryChooseSearchSuggestion(),
                "escape" => page.CloseSearchSuggestions(),
                _ => throw new ArgumentException("down|up|enter|escape")
            };
            return $"taken={taken} {Page.DescribeSearchSuggestions()}";
        });
        Verb("search-open", async args =>
        {
            var page = Page;
            var index = (int)Index(args, 0);
            if (index >= page.SearchResults.Length) return "no result";
            await page.OpenSearchResultAsync(page.SearchResults[index]);
            return Selected(page);
        });
        Verb("submit", async args =>
        {
            var page = Page;
            await page.SearchSubmittedAsync(string.Join(' ', args));
            return Selected(page);
        });
    }

    private static string FilterState(ToDoPageView page)
    {
        var vm = page.ViewModel;
        return $"filter='{vm.FilterText}' entry='{page.FilterEntry?.GetText()}' scope={vm.SelectedCompletionScope} important={vm.IsImportantTasksFilterSelected} "
            + $"sort={vm.SelectedSort} filtered={vm.IsFiltered} compact={page.IsFilterCompact} chips: {page.FilterChipText} rows={page.RowCount}";
    }
}
#endif
