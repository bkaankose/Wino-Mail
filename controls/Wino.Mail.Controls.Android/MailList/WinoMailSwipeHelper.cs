using Android.Graphics;
using AndroidX.RecyclerView.Widget;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.MailList;

/// <summary>
/// Swipe actions on mail rows through <see cref="ItemTouchHelper"/> (canvas Components: leading Archive, trailing
/// Delete, both configurable in Settings). The page decides which operation a swipe runs (the user's swipe preferences
/// and the ViewModel's rules, for example Unarchive in the archive folder) and runs it; the helper only draws the action
/// tile (shared <see cref="MailOperationDisplay"/> title and glyph) and reports the swipe. After a swipe the row slides
/// back; the ViewModel removes it when the operation moves it out of the folder.
/// </summary>
public sealed class WinoMailSwipeHelper : ItemTouchHelper.SimpleCallback
{
    private readonly WinoMailListAdapter _adapter;
    private readonly Func<MailListRow, bool, MailOperation?> _operation;
    private readonly Action<MailListRow, MailOperation> _invoked;
    private readonly Paint _fill = new();
    private readonly Paint _glyph = new(PaintFlags.AntiAlias);
    private readonly Paint _label = new(PaintFlags.AntiAlias);

    /// <param name="operation">The operation for a row and side (true = leading, a start-to-end swipe); null disables that side.</param>
    /// <param name="invoked">Runs the operation for the row.</param>
    public WinoMailSwipeHelper(WinoMailListAdapter adapter, Func<MailListRow, bool, MailOperation?> operation, Action<MailListRow, MailOperation> invoked)
        : base(0, ItemTouchHelper.Start | ItemTouchHelper.End)
    {
        _adapter = adapter;
        _operation = operation;
        _invoked = invoked;
        _glyph.TextAlign = Paint.Align.Center;
        _glyph.Color = Color.White;
        _label.TextAlign = Paint.Align.Center;
        _label.Color = Color.White;
        _label.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
    }

    /// <summary>Attaches a new helper to <paramref name="recyclerView"/>.</summary>
    public static ItemTouchHelper Attach(RecyclerView recyclerView, WinoMailListAdapter adapter,
        Func<MailListRow, bool, MailOperation?> operation, Action<MailListRow, MailOperation> invoked)
    {
        var helper = new ItemTouchHelper(new WinoMailSwipeHelper(adapter, operation, invoked));
        helper.AttachToRecyclerView(recyclerView);
        return helper;
    }

    /// <summary>The tile colour for an operation: archive green, delete red, flag amber, read state accent.</summary>
    public static Color ActionColor(global::Android.Content.Context context, MailOperation operation) => operation switch
    {
        MailOperation.Archive or MailOperation.UnArchive => WinoColors.Success(context),
        MailOperation.SoftDelete or MailOperation.HardDelete or MailOperation.MoveToJunk => WinoColors.Critical(context),
        MailOperation.SetFlag or MailOperation.ClearFlag => WinoColors.Caution(context),
        MailOperation.MarkAsRead or MailOperation.MarkAsUnread => WinoColors.Accent(context),
        _ => WinoColors.SecondaryText(context)
    };

    public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, RecyclerView.ViewHolder target) => false;

    public override int GetSwipeDirs(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder)
    {
        if (_adapter.IsSelectionMode || _adapter.RowAt(viewHolder.BindingAdapterPosition) is not { } row) return 0;
        int directions = 0;
        // ItemTouchHelper END is a start-to-end swipe (rightward in left-to-right layouts): the leading action.
        if (_operation(row, true) is not null) directions |= ItemTouchHelper.End;
        if (_operation(row, false) is not null) directions |= ItemTouchHelper.Start;
        return directions;
    }

    public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction)
    {
        int position = viewHolder.BindingAdapterPosition;
        if (_adapter.RowAt(position) is not { } row) return;
        bool leading = (direction & ItemTouchHelper.End) != 0;
        _adapter.NotifyItemChanged(position);
        if (_operation(row, leading) is { } operation) _invoked(row, operation);
    }

    public override void OnChildDraw(Canvas c, RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, float dX, float dY, int actionState, bool isCurrentlyActive)
    {
        var item = viewHolder.ItemView;
        if (actionState == ItemTouchHelper.ActionStateSwipe && dX != 0 && _adapter.RowAt(viewHolder.BindingAdapterPosition) is { } row
            && item.Context is { } context)
        {
            bool rightward = dX > 0;
            bool leading = item.LayoutDirection == global::Android.Views.LayoutDirection.Rtl ? !rightward : rightward;
            if (_operation(row, leading) is { } operation)
            {
                _fill.Color = ActionColor(context, operation);
                float left = rightward ? item.Left : item.Right + dX;
                float right = rightward ? item.Left + dX : item.Right;
                c.DrawRect(left, item.Top, right, item.Bottom, _fill);

                float tile = WinoUnits.DpF(context, 96);
                float centerX = rightward ? item.Left + tile / 2 : item.Right - tile / 2;
                float centerY = (item.Top + item.Bottom) / 2f;
                _glyph.SetTypeface(WinoIcons.Mono(context));
                _glyph.TextSize = WinoUnits.DpF(context, 22);
                _label.TextSize = WinoUnits.Sp(context, 11);
                c.Save();
                c.ClipRect(left, item.Top, right, item.Bottom);
                c.DrawText(WinoIcons.Glyph(MailOperationDisplay.GetGlyph(operation)), centerX, centerY - WinoUnits.DpF(context, 2), _glyph);
                c.DrawText(Title(operation), centerX, centerY + WinoUnits.DpF(context, 16), _label);
                c.Restore();
            }
        }
        base.OnChildDraw(c, recyclerView, viewHolder, dX, dY, actionState, isCurrentlyActive);
    }

    private static string Title(MailOperation operation)
        => operation == MailOperation.SetFlag ? global::Wino.Core.Domain.Translator.MailOperation_Flag : MailOperationDisplay.GetTitle(operation);
}
