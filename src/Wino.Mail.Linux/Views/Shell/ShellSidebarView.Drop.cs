using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell.Contracts;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// Drops on sidebar rows (macOS <c>ShellSidebarViewController.Drop</c>, Windows <c>ItemDroppedOnFolder</c>): one string
/// drop target per row that asks the <see cref="ISidebarDropHandler"/> whose drag is active, the drop highlight, and
/// autoscroll near the top and bottom edges while a drag is over the sidebar (GTK has none).
/// </summary>
internal sealed partial class ShellSidebarView
{
    private const int AutoscrollEdge = 28;

    private readonly List<ISidebarDropHandler> _dropHandlers = [];
    private readonly Dictionary<ShellSidebarCell, IDisposable> _dropCells = new(ReferenceEqualityComparer.Instance);
    private Gtk.Widget? _dropRow;
    private uint _autoscrollSource;
    private double _autoscrollSpeed;

    public void AddDropHandler(ISidebarDropHandler handler) => _dropHandlers.Add(handler);

    /// <summary>The list row widget that carries the drop highlight, or null.</summary>
    internal Gtk.Widget? DropRow => _dropRow;

    internal double AutoscrollSpeed => _autoscrollSpeed;

    private void SetUpDrop()
    {
        _dropHandlers.Add(new MailSidebarDropHandler(this));
        _dropHandlers.Add(new Contacts.ContactSidebarDropHandler(_error));
        var previousSetup = CellSetup;
        var previousTeardown = CellTeardown;
        CellSetup = (cell, listItem) =>
        {
            previousSetup?.Invoke(cell, listItem);
            DropCellSetup(cell);
        };
        CellTeardown = cell =>
        {
            previousTeardown?.Invoke(cell);
            DropCellTeardown(cell);
        };

        var scroller = Scroller;
        var motion = Gtk.DropControllerMotion.New();
        var moved = GtkSignals.Connect<Gtk.DropControllerMotion, Gtk.DropControllerMotion.MotionSignalArgs>(
            h => motion.OnMotion += h, h => motion.OnMotion -= h, (_, args) => AutoscrollAt(args.Y));
        var left = GtkSignals.Connect<Gtk.DropControllerMotion>(h => motion.OnLeave += h, h => motion.OnLeave -= h, (_, _) => SetAutoscroll(0));
        scroller.AddController(motion);
        _bindings.Own(new ActionDisposable(() =>
        {
            moved.Dispose();
            left.Dispose();
            scroller.RemoveController(motion);
            SetAutoscroll(0);
            foreach (var subscription in _dropCells.Values) subscription.Dispose();
            _dropCells.Clear();
            _dropHandlers.Clear();
            _dropRow = null;
        }));
    }

    private void DropCellSetup(ShellSidebarCell cell)
    {
        if (_disposed) return;
        var root = cell.Root;
        var target = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move | Gdk.DragAction.Copy);
        var enter = GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.EnterSignalArgs, Gdk.DragAction>(
            h => target.OnEnter += h, h => target.OnEnter -= h, (_, _) => Evaluate(cell), default);
        var motion = GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.MotionSignalArgs, Gdk.DragAction>(
            h => target.OnMotion += h, h => target.OnMotion -= h, (_, _) => Evaluate(cell), default);
        var leave = GtkSignals.Connect<Gtk.DropTarget>(h => target.OnLeave += h, h => target.OnLeave -= h, (_, _) => LeaveDrop(cell));
        var drop = GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.DropSignalArgs, bool>(
            h => target.OnDrop += h, h => target.OnDrop -= h, (_, args) => DropOn(cell, args.Value.GetString()), false);
        root.AddController(target);
        _dropCells[cell] = new ActionDisposable(() =>
        {
            enter.Dispose();
            motion.Dispose();
            leave.Dispose();
            drop.Dispose();
            root.RemoveController(target);
        });
    }

    private void DropCellTeardown(ShellSidebarCell cell)
    {
        if (!_dropCells.Remove(cell, out var subscription)) return;
        subscription.Dispose();
        if (_dropRow is not null && ReferenceEquals(_dropRow, cell.Root.GetParent())) SetDropRow(null);
    }

    private ISidebarDropHandler? ActiveDropHandler()
    {
        foreach (var handler in _dropHandlers)
        {
            if (handler.IsDragActive) return handler;
        }
        return null;
    }

    private bool IsSelectedForDrop(IMenuItem item) => ReferenceEquals(item, _selected);

    private Gdk.DragAction Evaluate(ShellSidebarCell cell)
    {
        if (ItemAt(cell.Root) is not { } item || ActiveDropHandler() is not { } handler)
        {
            SetDropRow(null);
            return default;
        }
        var verdict = handler.Validate(item, IsSelectedForDrop(item));
        handler.ShowCaption(verdict);
        SetDropRow(verdict.Action != default ? cell.Root.GetParent() : null);
        return verdict.Action;
    }

    private void LeaveDrop(ShellSidebarCell cell)
    {
        if (_dropRow is not null && ReferenceEquals(_dropRow, cell.Root.GetParent())) SetDropRow(null);
        ActiveDropHandler()?.ShowCaption(SidebarDropVerdict.None);
    }

    private bool DropOn(ShellSidebarCell cell, string? text)
    {
        SetDropRow(null);
        SetAutoscroll(0);
        return ItemAt(cell.Root) is { } item && ActiveDropHandler() is { } handler && handler.Drop(item, IsSelectedForDrop(item), text);
    }

    private void SetDropRow(Gtk.Widget? row)
    {
        if (ReferenceEquals(row, _dropRow)) return;
        _dropRow?.RemoveCssClass(WinoCssClass.DropTarget);
        _dropRow = row;
        row?.AddCssClass(WinoCssClass.DropTarget);
    }

    private void AutoscrollAt(double y)
    {
        if (ActiveDropHandler() is null)
        {
            SetAutoscroll(0);
            return;
        }
        var height = Scroller.GetHeight();
        if (y < AutoscrollEdge) SetAutoscroll(-Speed((AutoscrollEdge - y) / AutoscrollEdge));
        else if (y > height - AutoscrollEdge) SetAutoscroll(Speed((y - (height - AutoscrollEdge)) / AutoscrollEdge));
        else SetAutoscroll(0);
    }

    private static double Speed(double depth) => 4 + 16 * Math.Clamp(depth, 0, 1);

    /// <summary>Pixels per 16 ms tick; negative scrolls up, 0 stops.</summary>
    internal void SetAutoscroll(double speed)
    {
        _autoscrollSpeed = speed;
        if (speed == 0 || _autoscrollSource != 0 || _disposed) return;
        _autoscrollSource = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 16, AutoscrollTick);
    }

    private bool AutoscrollTick()
    {
        var active = ActiveDropHandler() is not null;
#if DEBUG
        active |= _forcedAutoscroll;
#endif
        if (_disposed || _autoscrollSpeed == 0 || !active)
        {
            _autoscrollSource = 0;
            _autoscrollSpeed = 0;
            return false;
        }
        var adjustment = Scroller.GetVadjustment();
        var next = Math.Clamp(adjustment.GetValue() + _autoscrollSpeed, adjustment.GetLower(), Math.Max(adjustment.GetLower(), adjustment.GetUpper() - adjustment.GetPageSize()));
        adjustment.SetValue(next);
        return true;
    }

#if DEBUG
    private bool _forcedAutoscroll;

    internal SidebarDropVerdict DebugValidateDrop(IMenuItem item)
    {
        if (ActiveDropHandler() is not { } handler) return SidebarDropVerdict.None;
        var verdict = handler.Validate(item, IsSelectedForDrop(item));
        handler.ShowCaption(verdict);
        return verdict;
    }

    internal bool DebugDrop(IMenuItem item, string text)
        => ActiveDropHandler() is { } handler && handler.Drop(item, IsSelectedForDrop(item), text);

    internal void DebugHighlight(IMenuItem? item)
        => SetDropRow(item is not null && CellOf(item) is { } cell ? cell.Root.GetParent() : null);

    internal void DebugAutoscroll(double speed)
    {
        _forcedAutoscroll = speed != 0;
        SetAutoscroll(speed);
    }
#endif

    private sealed class MailSidebarDropHandler(ShellSidebarView sidebar) : ISidebarDropHandler
    {
        public bool IsDragActive => MailDragPayload.Current.Count > 0;

        public SidebarDropVerdict Validate(IMenuItem item, bool isSelected)
        {
            if (item is not IBaseFolderMenuItem folder || !ShellMenuItemRules.IsEnabled(folder)) return SidebarDropVerdict.None;
            if (!MailDragPayload.CanDropOn(folder, isSelected, MailDragPayload.Current)) return new(default, Translator.DragCannotMoveHereCaption, true);
            return new(Gdk.DragAction.Move, string.Format(Translator.DragMoveToFolderCaption, folder.FolderName), false);
        }

        public void ShowCaption(SidebarDropVerdict verdict) => MailDragPayload.SetCaption(verdict.Caption, verdict.Refused);

        public bool Drop(IMenuItem item, bool isSelected, string? text)
        {
            if (MailDragPayload.Resolve(text) is not { } mails || item is not IBaseFolderMenuItem folder || !MailDragPayload.CanDropOn(folder, isSelected, mails)) return false;
            var moving = MailDragPayload.MailsFor(folder, mails);
            if (moving.Count == 0 || sidebar._viewModel.GetProvider(WinoApplicationMode.Mail) is not IMailShellClient mail) return false;
            Serilog.Log.Information("Mail drop: moving {Count} of {Total} mails to a folder.", moving.Count, mails.Length);
            _ = MoveAsync(mail, moving, folder);
            MailDragPayload.End();
            return true;
        }

        private async Task MoveAsync(IMailShellClient mail, List<MailCopy> moving, IBaseFolderMenuItem folder)
        {
            try { await mail.PerformMoveOperationAsync(moving, folder); }
            catch (Exception exception) { sidebar._error(exception); }
        }
    }
}
