using System.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>A recycled task list cell: maps the bound task into <see cref="WinoTaskCard"/>.</summary>
internal sealed class TaskCell : IWinoCell<TaskItemViewModel>
{
    private readonly Func<TaskItemViewModel, string?> _listColor;
    private readonly IDispatcher _dispatcher;

    public TaskCell(Func<TaskItemViewModel, string?> listColor, IDispatcher dispatcher,
        Action<TaskItemViewModel> toggleCompletion, Action<TaskItemViewModel> toggleImportance)
    {
        _listColor = listColor;
        _dispatcher = dispatcher;
        Card = new WinoTaskCard();
        Card.CompletionToggled += () =>
        {
            if (Item is { } task) toggleCompletion(task);
        };
        Card.ImportanceToggled += () =>
        {
            if (Item is { } task) toggleImportance(task);
        };
    }

    public Gtk.Widget Root => Card.Root;

    public WinoTaskCard Card { get; }

    public TaskItemViewModel? Item { get; private set; }

    /// <summary>The position in the sections model at the last bind.</summary>
    public uint Position { get; private set; }

    public Gtk.ListItem? ListItem { get; private set; }

    public void Bind(TaskItemViewModel item, WinoCellBinding binding)
    {
        Item = item;
        ListItem = binding.ListItem;
        Position = binding.Position;
        binding.ListItem.SetAccessibleLabel(item.AutomationName ?? string.Empty);
        Refresh();
        PropertyChangedEventHandler changed = (_, _) => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (!ReferenceEquals(Item, item)) return;
            Refresh();
            ListItem?.SetAccessibleLabel(item.AutomationName ?? string.Empty);
        });
        item.PropertyChanged += changed;
        binding.Scope.Own(new ActionDisposable(() => item.PropertyChanged -= changed));
    }

    public void Unbind()
    {
        Item = null;
        ListItem = null;
    }

    public void Refresh()
    {
        if (Item is not { Task: not null } item) return;
        Card.Apply(TaskRowPresentation.Describe(item, _listColor(item)));
    }
}
