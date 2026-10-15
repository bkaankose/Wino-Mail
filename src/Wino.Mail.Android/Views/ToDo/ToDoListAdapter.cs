using System.ComponentModel;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Wino.Mail.Controls.Android.ToDo;
using Wino.Mail.Controls.Core.ToDo;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.ToDo;

/// <summary>
/// The grouped task list (Windows ToDoPage task groups): a Material 3 list subheader ("Completed · 3") for each group that
/// shows a header, then its tasks as <see cref="WinoTaskRowView"/> rows. Rows re-map when their task changes; the page
/// supplies the row model mapping (shared <see cref="TaskRowPresentation"/>).
/// </summary>
internal sealed class ToDoListAdapter(Func<TaskItemViewModel, WinoTaskRowModel> map) : RecyclerView.Adapter
{
    private const int HeaderType = 0;
    private const int RowType = 1;

    private readonly record struct Entry(string Header, TaskItemViewModel? Task);

    private readonly List<Entry> _entries = [];
    private Guid? _selectedId;

    public event EventHandler<TaskItemViewModel>? TaskClicked;
    public event EventHandler<(TaskItemViewModel Task, View Anchor)>? TaskLongClicked;
    public event EventHandler<TaskItemViewModel>? CompletionToggled;
    public event EventHandler<TaskItemViewModel>? ImportanceToggled;

    public void SetGroups(IEnumerable<TaskGroup> groups)
    {
        _entries.Clear();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            if (group.ShowHeader) _entries.Add(new Entry($"{group.Key} · {group.HeaderCountText}", null));
            foreach (var task in group) _entries.Add(new Entry(string.Empty, task));
        }
        NotifyDataSetChanged();
    }

    public void SetSelected(Guid? id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _entries.Count;

    public override int GetItemViewType(int position) => _entries[position].Task is null ? HeaderType : RowType;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var context = parent.RequireContext();
        if (viewType == HeaderType)
        {
            var header = WinoTokens.Style(new TextView(context), WinoTextStyle.Subheader);
            header.SetTextColor(WinoColors.SecondaryText(context));
            header.Gravity = GravityFlags.CenterVertical;
            header.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height));
            WinoTokens.Pad(header, Resource.Dimension.wino_m3_subheader_padding, 0, Resource.Dimension.wino_m3_subheader_padding, 0);
            AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
            header.LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            return new Holder(header);
        }
        var row = new WinoTaskRowView(context) { LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) };
        var holder = new Holder(row);
        row.Click += (_, _) => { if (holder.Task is { } task) TaskClicked?.Invoke(this, task); };
        row.LongClick += (_, args) =>
        {
            args.Handled = holder.Task is not null;
            if (holder.Task is { } task) TaskLongClicked?.Invoke(this, (task, row));
        };
        row.CompletionToggled += (_, _) => { if (holder.Task is { } task) CompletionToggled?.Invoke(this, task); };
        row.ImportanceToggled += (_, _) => { if (holder.Task is { } task) ImportanceToggled?.Invoke(this, task); };
        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        if (holder is not Holder bound || position >= _entries.Count) return;
        var entry = _entries[position];
        bound.Follow(entry.Task, task => { if (bound.ItemView is WinoTaskRowView row) row.Apply(map(task)); });
        if (entry.Task is null)
        {
            if (bound.ItemView is TextView header) header.Text = entry.Header;
            return;
        }
        if (bound.ItemView is WinoTaskRowView row)
        {
            row.Apply(map(entry.Task));
            row.IsRowSelected = _selectedId == entry.Task.Id;
        }
    }

    public override void OnViewRecycled(Java.Lang.Object holder)
    {
        if (holder is Holder bound) bound.Follow(null, null);
        base.OnViewRecycled(holder);
    }

    public override void OnDetachedFromRecyclerView(RecyclerView recyclerView)
    {
        for (int index = 0; index < recyclerView.ChildCount; index++)
            if (recyclerView.GetChildAt(index) is { } child && recyclerView.GetChildViewHolder(child) is Holder holder) holder.Follow(null, null);
        base.OnDetachedFromRecyclerView(recyclerView);
    }

    private sealed class Holder(View view) : RecyclerView.ViewHolder(view)
    {
        private PropertyChangedEventHandler? _handler;

        public TaskItemViewModel? Task { get; private set; }

        /// <summary>Re-applies the row when its task changes (title, completion, importance, due, steps).</summary>
        public void Follow(TaskItemViewModel? task, Action<TaskItemViewModel>? apply)
        {
            if (Task is not null && _handler is not null) Task.PropertyChanged -= _handler;
            _handler = null;
            Task = task;
            if (task is null || apply is null) return;
            _handler = (_, _) => ItemView.Post(() => { if (ReferenceEquals(Task, task)) apply(task); });
            task.PropertyChanged += _handler;
        }
    }
}
