using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.ToDo;

/// <summary>
/// One task card (Windows TaskRowTemplate, macOS WinoTaskCardView): round check box, title, a metadata line (list,
/// due date in the error colour when overdue, steps, My Day in the accent, note) and the importance star. Built once
/// and switched by <see cref="Apply"/>, which returns at once for an unchanged model.
/// </summary>
public sealed class WinoTaskCard : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly WinoRoundCheckbox _check = new();
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _meta = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.Box _list = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    private readonly Gtk.Box _listDot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Label _listName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _due = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    private readonly Gtk.Label _dueText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _steps = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _myDay = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    private readonly Gtk.Label _myDayText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _note;
    private readonly Gtk.Button _star;
    private readonly IDisposable _starClicked;
    private string? _dotTint;
    private WinoTaskRowModel? _shown;

    public WinoTaskCard()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass(WinoCssClass.TaskCard);
        _root.Append(_check.Root);

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.SetCanTarget(false);
        _title.SetXalign(0);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        _title.SetSingleLineMode(true);
        text.Append(_title);

        _listDot.AddCssClass(WinoCssClass.TaskListDot);
        _listDot.SetValign(Gtk.Align.Center);
        _list.Append(_listDot);
        _list.Append(Caption(_listName));
        _meta.Append(_list);

        var dueGlyph = WinoIcons.Label(WinoIconGlyph.Calendar, 11);
        _due.Append(dueGlyph);
        _due.Append(Caption(_dueText));
        _meta.Append(_due);

        _meta.Append(Caption(_steps));

        var sun = WinoIcons.Label(WinoIconGlyph.WeatherSunny, 11);
        _myDay.AddCssClass("accent");
        _myDay.Append(sun);
        _myDay.Append(Caption(_myDayText));
        _meta.Append(_myDay);

        _note = WinoIcons.Label(WinoIconGlyph.Note, 11);
        _note.AddCssClass("dim-label");
        _meta.Append(_note);
        text.Append(_meta);
        _root.Append(text);

        _star = WinoIcons.Button(WinoIconGlyph.Star, Translator.ToDoPage_Important, 15);
        _star.SetSizeRequest(28, 28);
        _star.AddCssClass(WinoCssClass.TaskStar);
        _root.Append(_star);

        _check.Toggled += () => CompletionToggled?.Invoke();
        _starClicked = GtkSignals.OnClicked(_star, () => ImportanceToggled?.Invoke());
    }

    public Gtk.Widget Root => _root;

    public WinoRoundCheckbox CheckBox => _check;

    public Gtk.Button Star => _star;

    public WinoTaskRowModel? Model => _shown;

    public event Action? CompletionToggled;

    public event Action? ImportanceToggled;

    public void Apply(WinoTaskRowModel model)
    {
        if (model == _shown) return;
        var previous = _shown;
        _shown = model;

        if (previous?.Title != model.Title || previous?.IsCompleted != model.IsCompleted)
        {
            // GTK does not re-shape a label whose CSS text-decoration changed after it was laid out; markup does.
            if (model.IsCompleted) _title.SetMarkup("<s>" + GLib.Functions.MarkupEscapeText(model.Title, -1) + "</s>");
            else _title.SetText(model.Title);
            if (model.IsCompleted) _title.AddCssClass(WinoCssClass.TaskCompleted);
            else _title.RemoveCssClass(WinoCssClass.TaskCompleted);
        }
        _check.Checked = model.IsCompleted;
        _check.Enabled = model.IsEditable;
        _check.Label = model.CompletionActionText;

        var showList = !string.IsNullOrEmpty(model.ListName);
        _list.SetVisible(showList);
        if (showList)
        {
            _listName.SetText(model.ListName ?? string.Empty);
            WinoTint.Apply(_listDot, ref _dotTint, model.ListColorHex);
        }

        var showDue = !string.IsNullOrEmpty(model.DueText);
        _due.SetVisible(showDue);
        if (showDue) _dueText.SetText(model.DueText);
        if (model.IsOverdue) _due.AddCssClass("error");
        else _due.RemoveCssClass("error");

        _steps.SetVisible(!string.IsNullOrEmpty(model.StepSummary));
        _steps.SetText(model.StepSummary);

        _myDay.SetVisible(model.IsInMyDay);
        _myDayText.SetText(model.MyDayText);
        _note.SetVisible(model.HasNote);
        _meta.SetVisible(showList || showDue || !string.IsNullOrEmpty(model.StepSummary) || model.IsInMyDay || model.HasNote);

        if (model.IsImportant) _star.AddCssClass(WinoCssClass.ImportantOn);
        else _star.RemoveCssClass(WinoCssClass.ImportantOn);
        _star.SetSensitive(model.IsEditable);
        if (!string.IsNullOrEmpty(model.ImportanceActionText) && previous?.ImportanceActionText != model.ImportanceActionText)
            WinoA11y.LabelWithTooltip(_star, model.ImportanceActionText);
    }

    public void Dispose()
    {
        _starClicked.Dispose();
        _check.Dispose();
        CompletionToggled = null;
        ImportanceToggled = null;
    }

    private static Gtk.Label Caption(Gtk.Label label)
    {
        label.AddCssClass("caption");
        label.SetXalign(0);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetSingleLineMode(true);
        return label;
    }
}

/// <summary>A group header of the task list: the group key and its count.</summary>
public static class WinoTaskGroupHeader
{
    public static Gtk.Widget Create()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        box.AddCssClass(WinoCssClass.TaskGroupHeader);
        var key = Gtk.Label.New(string.Empty);
        key.AddCssClass("heading");
        key.SetXalign(0);
        key.SetEllipsize(Pango.EllipsizeMode.End);
        var count = Gtk.Label.New(string.Empty);
        count.AddCssClass("caption");
        count.AddCssClass("dim-label");
        box.Append(key);
        box.Append(count);
        return box;
    }

    public static void Apply(Gtk.Widget root, string? key, string? count)
    {
        if (root.GetFirstChild() is not Gtk.Label keyLabel || keyLabel.GetNextSibling() is not Gtk.Label countLabel) return;
        keyLabel.SetText(key ?? string.Empty);
        countLabel.SetText(count ?? string.Empty);
        WinoA11y.Label(root, string.IsNullOrEmpty(count) ? key : $"{key}, {count}");
    }
}
