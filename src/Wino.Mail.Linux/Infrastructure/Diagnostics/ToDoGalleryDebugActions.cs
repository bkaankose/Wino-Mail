#if DEBUG
using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-todo-gallery</c> <c>s</c>: <c>''</c> opens the window <c>To Do gallery</c> with every task card state, a
/// group header, the chip kinds, round check boxes and quick add; <c>'close'</c> closes it; a path writes the
/// gallery's state (card models, check and star names). Clicking a check box or star flips that card's model.
/// </summary>
internal static class ToDoGalleryDebugActions
{
    private const string WindowTitle = "To Do gallery";
    private static Adw.Window? _window;
    private static readonly List<IDisposable> Owned = [];
    private static readonly List<WinoTaskCard> Cards = [];

    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-todo-gallery", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            var argument = args.Parameter?.GetString(out var _) ?? string.Empty;
            try
            {
                if (argument == "close") Close();
                else if (argument.Length > 0) DebugActions.WriteText(argument, Describe());
                else Show(application);
                Serilog.Log.Information("Debug action debug-todo-gallery {Argument}: done", argument);
            }
            catch (Exception exception)
            {
                Serilog.Log.Error(exception, "Debug action debug-todo-gallery failed.");
            }
        };
        application.AddAction(action);
    }

    private static void Show(Adw.Application application)
    {
        if (_window is not null)
        {
            _window.Present();
            return;
        }
        var today = DateTime.Today;
        WinoTaskRowModel Model(string title) => new()
        {
            Title = title,
            MyDayText = Translator.ToDoPage_MyDay,
            ImportanceActionText = Translator.ToDoPage_Important,
            CompletionActionText = Translator.ToDoPage_ToggleCompleted,
        };
        WinoTaskRowModel[] models =
        [
            Model("Plain task"),
            Model("With list") with { ListName = "Groceries", ListColorHex = "#2E7D32" },
            Model("Due today") with { DueText = today.ToString("d") },
            Model("Overdue") with { DueText = today.AddDays(-3).ToString("d"), IsOverdue = true },
            Model("Steps") with { StepSummary = "2 of 5" },
            Model("My Day") with { IsInMyDay = true },
            Model("With a note") with { HasNote = true },
            Model(new string('L', 20) + " a very long title that must ellipsize instead of widening the window and the list around it, really")
                with { ListName = "Work", ListColorHex = "#1565C0", DueText = today.ToString("d"), StepSummary = "1 of 3", IsInMyDay = true, HasNote = true, IsImportant = true },
            Model("Completed") with { IsCompleted = true },
            Model("Important") with { IsImportant = true },
            Model("Read only") with { IsEditable = false },
        ];

        var list = Gtk.ListBox.New();
        list.AddCssClass(WinoCssClass.TaskList);
        list.SetSelectionMode(Gtk.SelectionMode.Single);
        foreach (var model in models)
        {
            var card = new WinoTaskCard();
            card.Apply(model);
            Cards.Add(card);
            Owned.Add(card);
            card.CompletionToggled += () => card.Apply((card.Model ?? model) with { IsCompleted = !(card.Model ?? model).IsCompleted });
            card.ImportanceToggled += () => card.Apply((card.Model ?? model) with { IsImportant = !(card.Model ?? model).IsImportant });
            list.Append(card.Root);
        }
        list.SelectRow(list.GetRowAtIndex(2));

        var header = WinoTaskGroupHeader.Create();
        WinoTaskGroupHeader.Apply(header, "carol3@stalwart.test", "3");

        var chips = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var menuChip = new WinoChipButton(WinoIconGlyph.List, Translator.ToDoPage_ScopeActive, Translator.ToDoPage_ShowScope, hasMenu: true);
        menuChip.Clicked += () => ContextMenu.Show(menuChip.Root, WinoMenus.Create()
            .Radio("scope", Translator.ToDoPage_ScopeActive, true, static () => { })
            .Radio("scope", Translator.ToDoPage_Completed, false, static () => { }).Build());
        var toggleOff = new WinoChipButton(WinoIconGlyph.Star, Translator.ToDoPage_Important, Translator.ToDoPage_Important);
        var toggleOn = new WinoChipButton(WinoIconGlyph.Star, Translator.ToDoPage_Important, Translator.ToDoPage_Important) { Checked = true };
        toggleOff.Clicked += () => toggleOff.Checked = !toggleOff.Checked;
        var disabled = new WinoChipButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_Clear, Translator.ToDoPage_ClearFilters) { Enabled = false };
        foreach (var chip in (WinoChipButton[])[menuChip, toggleOff, toggleOn, disabled])
        {
            Owned.Add(chip);
            chips.Append(chip.Root);
        }

        var checks = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var off = new WinoRoundCheckbox { Label = "off" };
        var on = new WinoRoundCheckbox { Checked = true, Label = "on" };
        var insensitive = new WinoRoundCheckbox { Enabled = false, Label = "disabled" };
        off.Toggled += () => off.Checked = !off.Checked;
        foreach (var check in (WinoRoundCheckbox[])[off, on, insensitive])
        {
            Owned.Add(check);
            checks.Append(check.Root);
        }

        var quickAdd = new WinoQuickAdd(Translator.ToDoPage_AddTask) { Placeholder = Translator.ToDoPage_AddTask };
        var quickAddOpen = new WinoQuickAdd(Translator.ToDoPage_AddTask) { Placeholder = Translator.ToDoPage_AddTask, ShowButton = true, Text = "Buy milk" };
        Owned.Add(quickAdd);
        Owned.Add(quickAddOpen);

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        column.SetMarginTop(12);
        column.SetMarginBottom(12);
        column.SetMarginStart(12);
        column.SetMarginEnd(12);
        column.Append(header);
        column.Append(list);
        column.Append(chips);
        column.Append(checks);
        column.Append(quickAdd.Root);
        column.Append(quickAddOpen.Root);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetChild(column);

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(scroller);
        var window = Adw.Window.New();
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(700, 760);
        window.SetContent(toolbar);
        window.SetApplication(application);
        window.OnCloseRequest += (_, _) =>
        {
            Release();
            return false;
        };
        _window = window;
        window.Present();
    }

    private static void Close()
    {
        var window = _window;
        Release();
        window?.Destroy();
    }

    private static void Release()
    {
        _window = null;
        foreach (var owned in Owned) owned.Dispose();
        Owned.Clear();
        Cards.Clear();
    }

    private static string Describe()
    {
        var text = new StringBuilder();
        foreach (var card in Cards)
        {
            if (card.Model is not { } model) continue;
            text.Append(model.Title.Length > 24 ? model.Title[..24] : model.Title)
                .Append(" completed=").Append(model.IsCompleted).Append(" important=").Append(model.IsImportant)
                .Append(" check=").Append(card.CheckBox.Checked).Append(" editable=").Append(model.IsEditable).Append('\n');
        }
        return text.ToString();
    }
}
#endif
