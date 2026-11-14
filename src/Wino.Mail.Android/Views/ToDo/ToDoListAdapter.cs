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
/// supplies the row model mapping (shared <see cref="TaskRowPresentation"/>). In manual sort
/// (<see cref="CanReorder"/>) rows show a drag handle; dragging it moves the row up or down inside its group (headers
/// stay put) and the drop raises <see cref="ReorderRequested"/> (Windows ToDoPage drag reorder). TalkBack gets Move up and
/// Move down actions on the rows instead.
/// </summary>
internal sealed class ToDoListAdapter(Func<TaskItemViewModel, WinoTaskRowModel> map) : RecyclerView.Adapter
{
    private const int HeaderType = 0;
    private const int RowType = 1;

    private readonly record struct Entry(string Header, TaskItemViewModel? Task, int Group);

    private readonly List<Entry> _entries = [];
    private Guid? _selectedId;
    private bool _canReorder;
    private IReadOnlyList<TaskGroup>? _pendingGroups;
    private ItemTouchHelper? _touchHelper;
    private TaskItemViewModel? _dragged;
    private int _dragStart = -1;

    public event EventHandler<TaskItemViewModel>? TaskClicked;
    public event EventHandler<(TaskItemViewModel Task, View Anchor)>? TaskLongClicked;
    public event EventHandler<TaskItemViewModel>? CompletionToggled;
    public event EventHandler<TaskItemViewModel>? ImportanceToggled;

    /// <summary>A row was dropped next to <c>Target</c> (after it when <c>InsertAfter</c>).</summary>
    public event EventHandler<(TaskItemViewModel Moved, TaskItemViewModel Target, bool InsertAfter)>? ReorderRequested;

    /// <summary>Manual sort: rows show the drag handle and offer Move up and Move down.</summary>
    public bool CanReorder
    {
        get => _canReorder;
        set
        {
            if (_canReorder == value) return;
            _canReorder = value;
            NotifyDataSetChanged();
        }
    }

    /// <summary>Lets rows be dragged by their handle in <paramref name="list"/>.</summary>
    public void AttachReorder(RecyclerView list)
    {
        _touchHelper = new ItemTouchHelper(new ReorderCallback(this));
        _touchHelper.AttachToRecyclerView(list);
    }

    public void DetachReorder()
    {
        _touchHelper?.AttachToRecyclerView(null);
        _touchHelper = null;
    }

    public void SetGroups(IEnumerable<TaskGroup> groups)
    {
        // A reload during a drag would pull the row from under the finger; it applies on drop.
        if (_dragged is not null)
        {
            _pendingGroups = groups.ToArray();
            return;
        }
        _entries.Clear();
        int index = 0;
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            if (group.ShowHeader) _entries.Add(new Entry($"{group.Key} · {group.HeaderCountText}", null, index));
            foreach (var task in group) _entries.Add(new Entry(string.Empty, task, index));
            index++;
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
        row.DragHandle.Touch += (_, args) =>
        {
            args.Handled = false;
            if (!_canReorder || holder.Task is null || args.Event?.ActionMasked != MotionEventActions.Down || _touchHelper is null) return;
            _touchHelper.StartDrag(holder);
            args.Handled = true;
        };
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
            row.ShowsDragHandle = _canReorder;
            ApplyMoveActions(row, position);
        }
    }

    private readonly Dictionary<View, (int Up, int Down)> _moveActions = [];

    private void ApplyMoveActions(View row, int position)
    {
        ApplyMoveActionsCleared(row);
        if (!_canReorder) return;
        int up = Neighbour(position, -1) >= 0
            ? AndroidX.Core.View.ViewCompat.AddAccessibilityAction(row, Wino.Core.Domain.Translator.MailFilters_MoveUp, new MoveCommand(() => MoveByAction(row, -1)))
            : View.NoId;
        int down = Neighbour(position, 1) >= 0
            ? AndroidX.Core.View.ViewCompat.AddAccessibilityAction(row, Wino.Core.Domain.Translator.MailFilters_MoveDown, new MoveCommand(() => MoveByAction(row, 1)))
            : View.NoId;
        _moveActions[row] = (up, down);
    }

    private bool MoveByAction(View row, int direction)
    {
        if (row.Parent is not RecyclerView list || list.GetChildViewHolder(row) is not { } holder) return false;
        int position = holder.BindingAdapterPosition;
        int neighbour = Neighbour(position, direction);
        if (position < 0 || neighbour < 0 || _entries[position].Task is not { } moved || _entries[neighbour].Task is not { } target) return false;
        ReorderRequested?.Invoke(this, (moved, target, direction > 0));
        return true;
    }

    /// <summary>The task row next to <paramref name="position"/> in its own group, or -1.</summary>
    private int Neighbour(int position, int direction)
    {
        int next = position + direction;
        if (position < 0 || position >= _entries.Count || next < 0 || next >= _entries.Count) return -1;
        return _entries[next].Task is not null && _entries[next].Group == _entries[position].Group ? next : -1;
    }

    private bool CanDrag(int position) => _canReorder && position >= 0 && position < _entries.Count && _entries[position].Task is not null;

    private bool Swap(int from, int to)
    {
        if (!CanDrag(from) || !CanDrag(to) || _entries[from].Group != _entries[to].Group) return false;
        (_entries[from], _entries[to]) = (_entries[to], _entries[from]);
        NotifyItemMoved(from, to);
        return true;
    }

    private void DragStarted(int position)
    {
        _dragged = position >= 0 && position < _entries.Count ? _entries[position].Task : null;
        _dragStart = position;
    }

    private void DragEnded(int position)
    {
        var moved = _dragged;
        int start = _dragStart;
        _dragged = null;
        _dragStart = -1;
        if (moved is not null && position >= 0 && position != start)
        {
            // Insert after the row above in the same group, or before the row below when the row lands first.
            if (Neighbour(position, -1) is int above and >= 0 && _entries[above].Task is { } previous)
                ReorderRequested?.Invoke(this, (moved, previous, true));
            else if (Neighbour(position, 1) is int below and >= 0 && _entries[below].Task is { } next)
                ReorderRequested?.Invoke(this, (moved, next, false));
        }
        if (_pendingGroups is { } pending)
        {
            _pendingGroups = null;
            SetGroups(pending);
        }
    }

    private sealed class MoveCommand(Func<bool> run) : Java.Lang.Object, AndroidX.Core.View.Accessibility.IAccessibilityViewCommand
    {
        public bool Perform(View view, AndroidX.Core.View.Accessibility.AccessibilityViewCommandCommandArguments? arguments) => run();
    }

    /// <summary>Vertical drags from the handle only; no swipes and no long-press drag (long press opens the task menu).</summary>
    private sealed class ReorderCallback(ToDoListAdapter adapter) : ItemTouchHelper.Callback
    {
        public override bool IsLongPressDragEnabled => false;

        public override bool IsItemViewSwipeEnabled => false;

        public override int GetMovementFlags(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder)
            => adapter.CanDrag(viewHolder.BindingAdapterPosition) ? MakeMovementFlags(ItemTouchHelper.Up | ItemTouchHelper.Down, 0) : 0;

        public override bool CanDropOver(RecyclerView recyclerView, RecyclerView.ViewHolder current, RecyclerView.ViewHolder target)
            => adapter.CanDrag(target.BindingAdapterPosition)
               && adapter._entries[target.BindingAdapterPosition].Group == adapter._entries[current.BindingAdapterPosition].Group;

        public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, RecyclerView.ViewHolder target)
            => adapter.Swap(viewHolder.BindingAdapterPosition, target.BindingAdapterPosition);

        public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction) { }

        public override void OnSelectedChanged(RecyclerView.ViewHolder? viewHolder, int actionState)
        {
            base.OnSelectedChanged(viewHolder, actionState);
            if (actionState == ItemTouchHelper.ActionStateDrag && viewHolder is not null)
            {
                adapter.DragStarted(viewHolder.BindingAdapterPosition);
                viewHolder.ItemView.Alpha = 0.9f;
                viewHolder.ItemView.TranslationZ = WinoTokens.PxF(viewHolder.ItemView.RequireContext(), Resource.Dimension.wino_space_xs);
            }
        }

        public override void ClearView(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder)
        {
            base.ClearView(recyclerView, viewHolder);
            viewHolder.ItemView.Alpha = 1f;
            viewHolder.ItemView.TranslationZ = 0;
            int position = viewHolder.BindingAdapterPosition;
            // ClearView runs inside the RecyclerView's layout pass; the reorder may rebuild the list.
            recyclerView.Post(() => adapter.DragEnded(position));
        }
    }

    public override void OnViewRecycled(Java.Lang.Object holder)
    {
        if (holder is Holder bound)
        {
            bound.Follow(null, null);
            ApplyMoveActionsCleared(bound.ItemView);
        }
        base.OnViewRecycled(holder);
    }

    public override void OnDetachedFromRecyclerView(RecyclerView recyclerView)
    {
        for (int index = 0; index < recyclerView.ChildCount; index++)
            if (recyclerView.GetChildAt(index) is { } child && recyclerView.GetChildViewHolder(child) is Holder holder) holder.Follow(null, null);
        base.OnDetachedFromRecyclerView(recyclerView);
    }

    private void ApplyMoveActionsCleared(View row)
    {
        if (!_moveActions.Remove(row, out var previous)) return;
        AndroidX.Core.View.ViewCompat.RemoveAccessibilityAction(row, previous.Up);
        AndroidX.Core.View.ViewCompat.RemoveAccessibilityAction(row, previous.Down);
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
