#if DEBUG
using System.Globalization;
using System.Text;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Task list verbs of <c>debug-todo</c>: rows, select i, select-many i j…, clear, add text (types into the quick add
/// entry and submits it as the Add button does), toggle i, star i (through the card buttons), focus-add, scroll top|end|px.
/// </summary>
internal static partial class ToDoDebugActions
{
    static partial void AddListVerbs()
    {
        Verb("rows", _ => Rows(Page));
        Verb("select", args =>
        {
            var page = Page;
            ListViews.Select(page.Selection, [Index(args, 0)]);
            return Selected(page);
        });
        Verb("select-many", args =>
        {
            var page = Page;
            ListViews.Select(page.Selection, [.. args.Select(static value => uint.Parse(value, CultureInfo.InvariantCulture))]);
            return Selected(page);
        });
        Verb("clear", _ =>
        {
            var page = Page;
            page.Selection.UnselectAll();
            return Selected(page);
        });
        Verb("add", async args =>
        {
            var page = Page;
            var text = string.Join(' ', args);
            page.QuickAdd.Focus();
            page.QuickAdd.Entry.SetText(text);
            var expanded = page.ViewModel.IsComposerExpanded;
            page.SubmitQuickAdd();
            for (var attempt = 0; attempt < 50 && !string.IsNullOrEmpty(page.ViewModel.ComposerText); attempt++) await Task.Delay(100);
            return $"expanded={expanded} composer='{page.ViewModel.ComposerText}' entry='{page.QuickAdd.Text}' rows={page.RowCount}";
        });
        Verb("toggle", args => Press(args, static cell => cell.Card.CheckBox.Button));
        Verb("star", args => Press(args, static cell => cell.Card.Star));
        Verb("focus-add", _ =>
        {
            var page = Page;
            page.QuickAdd.Focus();
            return $"focused={page.QuickAdd.IsFocused} expanded={page.ViewModel.IsComposerExpanded}";
        });
        Verb("scroll", args =>
        {
            var adjustment = Page.Scroller.GetVadjustment();
            var target = args.Length == 0 ? "end" : args[0];
            adjustment.SetValue(target switch
            {
                "top" => adjustment.GetLower(),
                "end" => adjustment.GetUpper() - adjustment.GetPageSize(),
                _ => adjustment.GetValue() + double.Parse(target, CultureInfo.InvariantCulture),
            });
            return $"value={adjustment.GetValue():0} upper={adjustment.GetUpper():0} page={adjustment.GetPageSize():0}";
        });
    }

    private static string Press(string[] args, Func<TaskCell, Gtk.Button> button)
    {
        var page = Page;
        var item = page.Sections.ItemAt(Index(args, 0)) ?? throw new ArgumentException("no row");
        if (page.Cells.CellOf(item) is not { } cell) throw new ArgumentException("row not bound");
        button(cell).Activate();
        return $"{item.Title} completed={item.IsCompleted} important={item.IsImportant}";
    }

    private static uint Index(string[] args, int position)
        => args.Length > position ? uint.Parse(args[position], CultureInfo.InvariantCulture) : throw new ArgumentException("missing index");

    private static string Selected(ToDoPageView page)
        => $"selected={page.ViewModel.SelectedTasks.Count} gtk={ListViews.SelectedPositions(page.Selection).Length} current={page.ViewModel.SelectedTask?.Title} layout={page.Layout.State} showsEnd={page.Layout.ShowsEnd}";

    private static string Rows(ToDoPageView page)
    {
        var text = new StringBuilder();
        text.Append("rows=").Append(page.RowCount).Append(" sections=").Append(page.SectionCount).Append(" headers=").Append(page.ShowsHeaders)
            .Append(" cells=").Append(page.Cells.CellCount).Append(" bound=").Append(page.Cells.BoundCount).Append(" | ");
        var rows = page.VisibleTasks;
        for (var index = 0; index < rows.Length; index++)
        {
            var item = rows[index];
            if (item is null) continue;
            text.Append(index).Append(':').Append(item.Title)
                .Append(item.IsCompleted ? " [done]" : string.Empty)
                .Append(item.IsImportant ? " [important]" : string.Empty)
                .Append(item.IsInMyDay ? " [myday]" : string.Empty)
                .Append(item.HasDueDate ? " due=" + item.DueDisplayText : string.Empty)
                .Append(page.Selection.IsSelected((uint)index) ? " [selected]" : string.Empty)
                .Append(" | ");
        }
        return text.ToString();
    }
}
#endif
