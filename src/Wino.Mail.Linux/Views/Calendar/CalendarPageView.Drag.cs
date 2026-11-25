using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Drag to move and resize (macOS CalendarPageViewController drag part): the surface refuses what the shared rules
/// refuse and says why; a drop goes through the ViewModel, which applies it in memory, asks the server and reverts on
/// failure, and the tiles are laid out again once it has.
/// </summary>
internal sealed partial class CalendarPageView
{
    partial void BuildDrag()
    {
        if (_surface is not { } surface) return;
        surface.CanDragItem = static item => CalendarTileMapper.DragRefusal(item) == CalendarDragRefusal.None;
        EventHandler<CalendarItemEventArgs> refused = (_, args) => SurfaceItemDragRefused(args);
        EventHandler<CalendarItemMoveRequestedEventArgs> moved = (_, args) =>
        {
            CloseQuickEvent();
            if (args.Item is CalendarItemViewModel item) Observe(ChangeTimesAsync(ViewModel.MoveCalendarItemAsync(item, args.Start)));
        };
        EventHandler<CalendarItemResizeRequestedEventArgs> resized = (_, args) =>
        {
            CloseQuickEvent();
            if (args.Item is CalendarItemViewModel item) Observe(ChangeTimesAsync(ViewModel.ResizeCalendarItemAsync(item, args.End)));
        };
        _activated.Add(() =>
        {
            surface.ItemDragRefused += refused;
            surface.ItemMoveRequested += moved;
            surface.ItemResizeRequested += resized;
#if DEBUG
            RegisterDragDebugVerbs(surface);
#endif
        });
        _deactivating.Add(() =>
        {
            surface.FinishPreviewDrag(commit: false);
            surface.ItemDragRefused -= refused;
            surface.ItemMoveRequested -= moved;
            surface.ItemResizeRequested -= resized;
            return Task.CompletedTask;
        });
    }

    /// <summary>Windows refuses the drag of a locked or read-only event; the Mac also says why.</summary>
    private void SurfaceItemDragRefused(CalendarItemEventArgs args)
    {
        CloseQuickEvent();
        switch (CalendarTileMapper.DragRefusal(args.Item))
        {
            case CalendarDragRefusal.ReadOnlyCalendar:
                Messenger.Send(new InfoBarMessageRequested(InfoBarMessageType.Warning, Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message));
                break;
            case CalendarDragRefusal.NotAllowed:
                Messenger.Send(new InfoBarMessageRequested(InfoBarMessageType.Warning, Translator.CalendarDragDropMoveNotAllowedTitle, Translator.CalendarDragDropMoveNotAllowedMessage));
                break;
        }
    }

    /// <summary>The ViewModel changes the item in place; the tiles are laid out again once it has, also after a revert.</summary>
    private async Task ChangeTimesAsync(Task change)
    {
        try { await change; }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (IsReleased || _surface is not { } surface) return;
                surface.ReloadLayout();
                surface.RefreshTiles();
            });
        }
    }
}
