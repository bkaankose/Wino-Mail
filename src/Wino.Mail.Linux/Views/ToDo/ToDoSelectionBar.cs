using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The drawer's bulk panel while several tasks are selected (Windows multi-selection pane, macOS BuildMultiPane): the
/// count, a short body and Complete, Reopen, Mark important and Delete for every selected task.
/// </summary>
internal sealed class ToDoSelectionBar : IDisposable
{
    private readonly ToDoPageViewModel _viewModel;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Button _back = Gtk.Button.NewWithLabel(Translator.ToDoPage_Back);
    private readonly Gtk.Button _close;
    private readonly Gtk.Label _count = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button[] _actions;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<string> _titles = [];

    public ToDoSelectionBar(ToDoPageViewModel viewModel, Action<Task> observe)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        top.SetMarginTop(8);
        top.SetMarginStart(8);
        top.SetMarginEnd(8);
        _back.AddCssClass("flat");
        _back.SetVisible(false);
        _back.SetHexpand(true);
        _back.SetHalign(Gtk.Align.Start);
        WinoA11y.Rename(_back, Translator.ToDoPage_Back);
        top.Append(_back);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        top.Append(spacer);
        _close = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.ToDoPage_CloseDetails, 13);
        top.Append(_close);
        _root.Append(top);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        body.SetValign(Gtk.Align.Center);
        body.SetHalign(Gtk.Align.Center);
        body.SetVexpand(true);
        body.SetMarginStart(24);
        body.SetMarginEnd(24);
        var glyph = WinoIcons.Label(WinoIconGlyph.MultiSelect, 36);
        glyph.AddCssClass("accent");
        body.Append(glyph);
        _count.AddCssClass("title-3");
        _count.SetWrap(true);
        _count.SetJustify(Gtk.Justification.Center);
        body.Append(_count);
        var text = Gtk.Label.New(Translator.ToDoPage_SelectedTasksBody);
        text.AddCssClass("dim-label");
        text.SetWrap(true);
        text.SetJustify(Gtk.Justification.Center);
        text.SetMaxWidthChars(32);
        body.Append(text);

        var buttons = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        buttons.SetMarginTop(8);
        _actions =
        [
            Action(buttons, WinoIconGlyph.Checkmark, Translator.ToDoPage_CompleteSelectedTasks, () => observe(viewModel.CompleteSelectedTasksCommand.ExecuteAsync(null))),
            Action(buttons, WinoIconGlyph.ArrowReset, Translator.ToDoPage_ReopenSelectedTasks, () => observe(viewModel.ReopenSelectedTasksCommand.ExecuteAsync(null))),
            Action(buttons, WinoIconGlyph.Star, Translator.ToDoPage_MarkSelectedTasksImportant, () => observe(viewModel.MarkSelectedTasksImportantCommand.ExecuteAsync(null))),
            Action(buttons, WinoIconGlyph.Delete, Translator.ToDoPage_DeleteSelectedTasks, () => observe(viewModel.DeleteSelectedTasksCommand.ExecuteAsync(null)), destructive: true),
        ];
        body.Append(buttons);
        _root.Append(body);

        _subscriptions.Add(GtkSignals.OnClicked(_back, () => viewModel.BackToTaskListCommand.Execute(null)));
        _subscriptions.Add(GtkSignals.OnClicked(_close, () => viewModel.CloseDetailCommand.Execute(null)));
    }

    public Gtk.Widget Root => _root;

    internal IReadOnlyList<Gtk.Button> Actions => _actions;

    public void SetCompact(bool compact)
    {
        _back.SetVisible(compact);
        _close.SetVisible(!compact);
    }

    public void Refresh()
    {
        _count.SetText(_viewModel.SelectedTaskCountText ?? string.Empty);
        var enabled = _viewModel.CanEditSelectedTasks;
        foreach (var action in _actions) action.SetSensitive(enabled);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private Gtk.Button Action(Gtk.Box parent, WinoIconGlyph glyph, string text, Action run, bool destructive = false)
    {
        var button = Gtk.Button.New();
        var content = WinoIcons.ButtonContent(glyph, text, 14);
        content.SetHalign(Gtk.Align.Center);
        button.SetChild(content);
        button.AddCssClass("pill");
        if (destructive) button.AddCssClass("destructive-action");
        WinoA11y.Rename(button, text);
        _titles.Add(text);
        _subscriptions.Add(GtkSignals.OnClicked(button, run));
        parent.Append(button);
        return button;
    }

#if DEBUG
    internal string Describe()
    {
        var text = new StringBuilder();
        text.Append("count='").Append(_count.GetText()).Append("' back=").Append(_back.GetVisible()).Append(" close=").Append(_close.GetVisible());
        for (var index = 0; index < _actions.Length; index++)
            text.Append(" | ").Append(_titles[index]).Append(" sensitive=").Append(_actions[index].GetSensitive());
        return text.ToString();
    }
#endif
}
