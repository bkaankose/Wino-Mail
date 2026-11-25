using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Hover actions (macOS <c>UpdateHover</c>, Windows <c>HoverActionsControl</c>): the bar of the row under the pointer,
/// with the actions, position, size and animation of the Message list settings. Never in select mode, during a drag or
/// on a busy row; kept on the row whose context menu is open.
/// </summary>
internal sealed partial class MailListView
{
    private readonly Dictionary<MailRowCell, IDisposable> _hoverCells = new(ReferenceEqualityComparer.Instance);
    private MailRowCell? _hoveredCell;
    private MailListRow? _forcedHoverRow;

    partial void SetUpHoverActions()
    {
        var self = _self;
        var preferences = ViewModel.PreferencesService;
        EventHandler<string> changed = (_, name) =>
        {
            if (name is nameof(IPreferencesService.IsHoverActionsEnabled) or nameof(IPreferencesService.HoverActionButtonSize)
                or nameof(IPreferencesService.HoverActionPosition) or nameof(IPreferencesService.HoverActionAnimation))
                self.Value?.RunOnUI(() => self.Value?.Cells.ForEachBound((cell, _) =>
                {
                    self.Value?.ApplyHoverPreferences(cell);
                    self.Value?.UpdateHover(cell, false);
                }));
        };
        preferences.PreferenceChanged += changed;
        Bindings.Own(new ActionDisposable(() =>
        {
            preferences.PreferenceChanged -= changed;
            if (self.Value is not { } view) return;
            foreach (var subscription in view._hoverCells.Values) subscription.Dispose();
            view._hoverCells.Clear();
            view._hoveredCell = null;
        }));
        Bind.Bind(nameof(MailListPageViewModel.MailActionResolver), static vm => vm.MailActionResolver, _ => RefreshHoverActions());
        Bind.Bind(nameof(MailListPageViewModel.IsMultiSelectionModeEnabled), static vm => vm.IsMultiSelectionModeEnabled, _ => UpdateAllHover());
        Bind.Bind(nameof(MailListPageViewModel.IsDragInProgress), static vm => vm.IsDragInProgress, _ => UpdateAllHover());
        ContextMenuRowChanged += UpdateAllHover;
    }

    partial void HoverCellSetup(MailRowCell cell)
    {
        var self = _self;
        ApplyHoverPreferences(cell);
        var motion = Gtk.EventControllerMotion.New();
        var enter = GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.EnterSignalArgs>(h => motion.OnEnter += h, h => motion.OnEnter -= h, (controller, _) =>
        {
            if (self.Value is not { } view || controller.GetCurrentEventDevice()?.GetSource() == Gdk.InputSource.Touchscreen) return;
            view._hoveredCell = cell;
            view.UpdateHover(cell);
        });
        var leave = GtkSignals.Connect<Gtk.EventControllerMotion>(h => motion.OnLeave += h, h => motion.OnLeave -= h, (_, _) =>
        {
            if (self.Value is not { } view) return;
            if (ReferenceEquals(view._hoveredCell, cell)) view._hoveredCell = null;
            view.UpdateHover(cell);
        });
        cell.Root.AddController(motion);
        Action<HoverActionKind> invoked = kind =>
        {
            if (self.Value is { IsReleased: false } view && cell.Item is { } row)
                view.Observe(view.ViewModel.ExecuteHoverActionCommand.ExecuteAsync(new HoverActionCommandRequest(kind, row)));
        };
        cell.Hover.Invoked += invoked;
        _hoverCells[cell] = new ActionDisposable(() =>
        {
            enter.Dispose();
            leave.Dispose();
            cell.Hover.Invoked -= invoked;
            cell.Root.RemoveController(motion);
        });
    }

    partial void HoverCellTeardown(MailRowCell cell)
    {
        if (_hoverCells.Remove(cell, out var subscription)) subscription.Dispose();
        if (ReferenceEquals(_hoveredCell, cell)) _hoveredCell = null;
    }

    partial void HoverCellRefreshed(MailRowCell cell, WinoMailRowModel model)
    {
        if (cell.Item is { } row) cell.Hover.SetActions(DescribeHover(row, model));
    }

    partial void HoverCellBound(MailRowCell cell, MailListRow row) => UpdateHover(cell, false);

    /// <summary>Shows the hover actions of one row without a pointer; null clears the forced row.</summary>
    internal void ForceHover(MailListRow? row)
    {
        _forcedHoverRow = row;
        UpdateAllHover();
    }

    private void RefreshHoverActions()
    {
        if (IsReleased) return;
        Cells.ForEachBound((cell, entry) =>
        {
            if (cell.Content is { } model) cell.Hover.SetActions(DescribeHover(entry.Row, model));
            UpdateHover(cell, false);
        });
    }

    private void UpdateAllHover()
    {
        if (!IsReleased) Cells.ForEachBound((cell, _) => UpdateHover(cell));
    }

    private void UpdateHover(MailRowCell cell, bool animate = true)
    {
        var row = cell.Item;
        var shown = row is not null
            && cell.Hover.HasActions
            && ViewModel.PreferencesService.IsHoverActionsEnabled
            && !ViewModel.IsMultiSelectionModeEnabled
            && !ViewModel.IsDragInProgress
            && cell.Content is not { IsBusy: true }
            && !cell.Swipe.IsActive
            && (ReferenceEquals(_hoveredCell, cell) || ReferenceEquals(_forcedHoverRow, row) || ReferenceEquals(ContextMenuRow, row));
        cell.Hover.SetShown(shown, animate);
    }

    private void ApplyHoverPreferences(MailRowCell cell)
    {
        var preferences = ViewModel.PreferencesService;
        cell.Hover.SetButtonSize(preferences.HoverActionButtonSize);
        cell.Hover.SetPosition(preferences.HoverActionPosition switch
        {
            MailHoverActionPosition.RightTop => HoverActionPosition.RightTop,
            MailHoverActionPosition.RightBottom => HoverActionPosition.RightBottom,
            MailHoverActionPosition.TopCenter => HoverActionPosition.TopCenter,
            MailHoverActionPosition.BottomCenter => HoverActionPosition.BottomCenter,
            _ => HoverActionPosition.RightCenter,
        });
        cell.Hover.SetAnimation(preferences.HoverActionAnimation switch
        {
            MailHoverActionAnimation.Slide => HoverActionAnimation.Slide,
            MailHoverActionAnimation.NoAnimation => HoverActionAnimation.NoAnimation,
            _ => HoverActionAnimation.Popup,
        });
    }

    private WinoHoverAction[] DescribeHover(MailListRow row, WinoMailRowModel model)
    {
        HoverActionSet set;
        if (ViewModel.MailActionResolver is { } resolver) set = resolver.ResolveHoverActions(row);
        else
        {
            var defaults = MailListActionPreferences.GetDefaultHoverActions(ViewModel.PreferencesService);
            set = new HoverActionSet(MailListActionResolver.ToHoverActionKind(defaults.Left),
                MailListActionResolver.ToHoverActionKind(defaults.Center), MailListActionResolver.ToHoverActionKind(defaults.Right));
        }
        var actions = new List<WinoHoverAction>(3);
        foreach (var kind in (HoverActionKind[])[set.Left, set.Center, set.Right])
        {
            if (Describe(kind, model) is { } action) actions.Add(action);
        }
        return [.. actions];
    }

    private static WinoHoverAction? Describe(HoverActionKind kind, WinoMailRowModel model) => kind switch
    {
        HoverActionKind.Archive => new(kind, WinoIconGlyph.Archive, Translator.MailOperation_Archive),
        HoverActionKind.Delete => new(kind, WinoIconGlyph.Delete, Translator.MailOperation_Delete),
        HoverActionKind.ToggleFlag => model.IsFlagged
            ? new(kind, WinoIconGlyph.ClearFlag, Translator.MailOperation_ClearFlag)
            : new(kind, WinoIconGlyph.Flag, Translator.MailOperation_SetFlag),
        HoverActionKind.ToggleRead => model.IsUnread
            ? new(kind, WinoIconGlyph.MarkRead, Translator.MailOperation_MarkAsRead)
            : new(kind, WinoIconGlyph.MarkUnread, Translator.MailOperation_MarkAsUnread),
        HoverActionKind.MoveToJunk => new(kind, WinoIconGlyph.Blocked, Translator.MailOperation_MoveJunk),
        _ => null,
    };

#if DEBUG
    internal MailRowCell? DebugHoverCell(MailListRow row)
        => _byRow.TryGetValue(row, out var entry) ? Cells.CellOf(entry) : null;

    internal string DebugHoverState()
    {
        var lines = new System.Text.StringBuilder();
        Cells.ForEachBound((cell, entry) =>
        {
            var subject = entry.Row.SourceItem is MailItemViewModel item ? item.Subject : entry.Row.ThreadKey;
            var bounds = cell.Hover.Root.ComputeBounds(cell.Root, out var rect) ? $"{rect.GetX():F0},{rect.GetY():F0} {rect.GetWidth():F0}x{rect.GetHeight():F0} cell={cell.Root.GetWidth()}x{cell.Root.GetHeight()} align={cell.Hover.Root.GetHalign()}/{cell.Hover.Root.GetValign()}" : "?";
            lines.Append(subject).Append('|').Append(cell.Hover.IsShown).Append('|').Append(cell.Hover.HasActions).Append('|').Append(cell.Hover.Describe()).Append('|').Append(bounds).Append('\n');
        });
        return lines.ToString();
    }
#endif
}
