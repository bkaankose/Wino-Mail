using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Tasks;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// A task prefilled from a mail or an event (macOS <c>CreateTaskFromItemSheet</c>, Windows CreateTaskFromItemDialog):
/// title, notes, list and a due date from No due date, Today, Tomorrow, Next week and the item's own date.
/// </summary>
internal sealed class CreateTaskFromItemDialog
{
    private readonly TaskFromItemDraft _draft;
    private readonly FormDialog _form = new(Translator.ConnectedItems_CreateTaskDialogTitle, null, Translator.Buttons_Create);
    private readonly Adw.EntryRow _title = Adw.EntryRow.New();
    private readonly Gtk.TextView _notes = Gtk.TextView.New();
    private readonly Adw.ComboRow _list = Adw.ComboRow.New();
    private readonly Adw.ComboRow _due = Adw.ComboRow.New();
    private readonly DueChoice[] _dueChoices;

    private CreateTaskFromItemDialog(TaskFromItemDraft draft, string[] listLabels)
    {
        _draft = draft;
        _title.SetUseMarkup(false);
        _title.SetTitle(Translator.ConnectedItems_TitleHeader);
        _title.SetText(draft.Title ?? string.Empty);
        _title.SetActivatesDefault(true);

        _notes.SetWrapMode(Gtk.WrapMode.WordChar);
        _notes.SetTopMargin(6);
        _notes.SetBottomMargin(6);
        _notes.SetLeftMargin(8);
        _notes.SetRightMargin(8);
        _notes.GetBuffer().SetText(draft.Notes ?? string.Empty, -1);
        WinoA11y.Label(_notes, Translator.ConnectedItems_NotesHeader);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetMinContentHeight(96);
        scroller.SetMaxContentHeight(180);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetChild(_notes);
        var frame = Gtk.Frame.New(null);
        frame.AddCssClass("view");
        frame.SetChild(scroller);
        var notesCaption = Gtk.Label.New(Translator.ConnectedItems_NotesHeader);
        notesCaption.AddCssClass("heading");
        notesCaption.SetXalign(0);
        var notes = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        notes.Append(notesCaption);
        notes.Append(frame);

        _list.SetUseMarkup(false);
        _list.SetTitle(Translator.ConnectedItems_ListHeader);
        _list.SetModel(Gtk.StringList.New(listLabels));
        var selectedList = draft.SelectedListId is { } id ? Math.Max(0, IndexOf(draft.Lists, id)) : 0;
        if (listLabels.Length > 0) _list.SetSelected((uint)selectedList);

        _dueChoices = BuildDueChoices(draft.DueDate);
        _due.SetUseMarkup(false);
        _due.SetTitle(Translator.ConnectedItems_DueHeader);
        _due.SetModel(Gtk.StringList.New(Array.ConvertAll(_dueChoices, static choice => choice.Label)));
        _due.SetSelected((uint)Math.Max(0, Array.FindIndex(_dueChoices, choice => choice.Date == draft.DueDate?.Date)));

        var title = _form.AddGroup();
        title.Add(_title);
        _form.Add(notes);
        var options = _form.AddGroup();
        options.Add(_list);
        options.Add(_due);

        _form.CanConfirm = () => _title.GetText().Trim().Length > 0 && SelectedList is not null;
        _form.Own(GtkSignals.OnChanged(_title, _form.Refresh));
        _form.Own(GtkSignals.OnNotify(_list, "selected", _form.Refresh));
        _form.SetInitialFocus(_title);
    }

#if DEBUG
    internal static CreateTaskFromItemDialog? DebugCurrent { get; private set; }
#endif

    private AccountTaskList? SelectedList
    {
        get
        {
            var index = (int)_list.GetSelected();
            return index >= 0 && index < _draft.Lists.Count ? _draft.Lists[index] : null;
        }
    }

    public static async Task<TaskFromItemResult?> ShowAsync(Gtk.Widget owner, TaskFromItemDraft draft, string[] listLabels)
    {
        if (draft.Lists.Count == 0) return null;
        var dialog = new CreateTaskFromItemDialog(draft, listLabels);
#if DEBUG
        DebugCurrent = dialog;
#endif
        try
        {
            if (!await dialog._form.PresentAsync(owner)) return null;
            return dialog.Result();
        }
        finally
        {
#if DEBUG
            if (ReferenceEquals(DebugCurrent, dialog)) DebugCurrent = null;
#endif
        }
    }

    private TaskFromItemResult? Result()
    {
        var listIndex = (int)_list.GetSelected();
        var dueIndex = (int)_due.GetSelected();
        if (listIndex < 0 || listIndex >= _draft.Lists.Count || dueIndex < 0 || dueIndex >= _dueChoices.Length) return null;
        var buffer = _notes.GetBuffer();
        buffer.GetBounds(out var start, out var end);
        var notes = buffer.GetText(start, end, false) ?? string.Empty;
        return new TaskFromItemResult(_title.GetText().Trim(), notes, _draft.Lists[listIndex], _dueChoices[dueIndex].Date);
    }

    private static int IndexOf(IReadOnlyList<AccountTaskList> lists, Guid id)
    {
        for (var index = 0; index < lists.Count; index++)
        {
            if (lists[index].Id == id) return index;
        }
        return -1;
    }

    private static DueChoice[] BuildDueChoices(DateTime? suggested)
    {
        var today = DateTime.Today;
        var choices = new List<DueChoice>
        {
            new(Translator.ConnectedItems_NoDueDate, null),
            new(Translator.ToDoPage_DuePresetToday, today),
            new(Translator.ToDoPage_DuePresetTomorrow, today.AddDays(1)),
            new(Translator.ToDoPage_DuePresetNextWeek, today.AddDays(7)),
        };
        if (suggested?.Date is { } date && choices.TrueForAll(choice => choice.Date != date))
            choices.Insert(1, new DueChoice(date.ToString("D", CultureInfo.CurrentCulture), date));
        return [.. choices];
    }

    private sealed record DueChoice(string Label, DateTime? Date);

#if DEBUG
    internal string Describe()
    {
        var buffer = _notes.GetBuffer();
        buffer.GetBounds(out var start, out var end);
        var lists = (Gtk.StringList?)_list.GetModel();
        return $"title=\"{_title.GetText()}\" notes={buffer.GetText(start, end, false)?.Length ?? 0} list={_list.GetSelected()}:{lists?.GetString(_list.GetSelected())} " +
               $"due={_due.GetSelected()}:{_dueChoices.ElementAtOrDefault((int)_due.GetSelected())?.Label} dues=[{string.Join("; ", _dueChoices.Select(static c => c.Label))}] lists={_draft.Lists.Count}";
    }

    internal void DebugSet(string title, int list, int due)
    {
        _title.SetText(title);
        if (list >= 0) _list.SetSelected((uint)list);
        if (due >= 0) _due.SetSelected((uint)due);
    }

    internal void DebugConfirm() => _form.DebugConfirm();

    internal void DebugCancel() => _form.Close(false);
#endif
}
