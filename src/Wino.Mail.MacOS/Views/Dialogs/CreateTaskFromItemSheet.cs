using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Tasks;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// Create a task from a mail or an event (Windows CreateTaskFromItemDialog): title, notes, the writable list and the due date.
/// Create stays disabled until there is a title and a list.
/// </summary>
internal sealed class CreateTaskFromItemSheet
{
    private readonly TaskFromItemDraft _draft;
    private readonly FormSheet _form;
    private readonly NSTextField _title;
    private readonly NSTextView _notes;
    private readonly NSPopUpButton _list;
    private readonly NSPopUpButton _due;
    private readonly List<DueChoice> _dueChoices;

    public CreateTaskFromItemSheet(TaskFromItemDraft draft, IReadOnlyList<string> listLabels)
    {
        _draft = draft;
        _form = new FormSheet(Translator.ConnectedItems_CreateTaskDialogTitle, null, Translator.Buttons_Create);

        _title = _form.AddTextField(Translator.ConnectedItems_TitleHeader, draft.Title);

        _notes = new NSTextView { Font = NSFont.SystemFontOfSize(13), RichText = false, TextContainerInset = new CGSize(4, 6) };
        _notes.Value = draft.Notes ?? string.Empty;
        _notes.TextContainer!.WidthTracksTextView = true;
        var notesScroll = new NSScrollView
        {
            DocumentView = _notes,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        notesScroll.HeightAnchor.ConstraintEqualTo(88).Active = true;
        notesScroll.WidthAnchor.ConstraintGreaterThanOrEqualTo(240).Active = true;
        _form.AddRow(Translator.ConnectedItems_NotesHeader, notesScroll);

        var selectedList = Math.Max(0, draft.Lists.Select((list, index) => (list, index)).FirstOrDefault(pair => pair.list.Id == draft.SelectedListId).index);
        _list = _form.AddPopUp(Translator.ConnectedItems_ListHeader, listLabels, selectedList);

        _dueChoices = BuildDueChoices(draft.DueDate);
        var selectedDue = Math.Max(0, _dueChoices.FindIndex(choice => choice.Date == draft.DueDate?.Date));
        _due = _form.AddPopUp(Translator.ConnectedItems_DueHeader, _dueChoices.Select(choice => choice.Label), selectedDue);

        _form.CanConfirm = () => _list.IndexOfSelectedItem >= 0 && _title.StringValue.Trim().Length > 0;
    }

    public async Task<TaskFromItemResult?> PresentAsync(NSWindow parent)
    {
        if (!await _form.PresentAsync(parent)) return null;

        var listIndex = (int)_list.IndexOfSelectedItem;
        var dueIndex = (int)_due.IndexOfSelectedItem;
        if (listIndex < 0 || listIndex >= _draft.Lists.Count || dueIndex < 0 || dueIndex >= _dueChoices.Count)
            return null;

        return new TaskFromItemResult(_title.StringValue.Trim(), _notes.Value ?? string.Empty, _draft.Lists[listIndex], _dueChoices[dueIndex].Date);
    }

    private static List<DueChoice> BuildDueChoices(DateTime? suggested)
    {
        var today = DateTime.Today;
        var choices = new List<DueChoice>
        {
            new(Translator.ConnectedItems_NoDueDate, null),
            new(Translator.ToDoPage_DuePresetToday, today),
            new(Translator.ToDoPage_DuePresetTomorrow, today.AddDays(1)),
            new(Translator.ToDoPage_DuePresetNextWeek, today.AddDays(7))
        };

        if (suggested?.Date is { } date && choices.All(choice => choice.Date != date))
            choices.Insert(1, new DueChoice(date.ToString("D", CultureInfo.CurrentCulture), date));

        return choices;
    }

    private sealed record DueChoice(string Label, DateTime? Date);
}
