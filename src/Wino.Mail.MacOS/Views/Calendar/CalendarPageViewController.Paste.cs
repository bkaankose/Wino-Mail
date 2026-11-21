using AppKit;
using Wino.Core.Domain;
using Wino.Mail.Controls.AppKit.Calendar;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>Paste on empty calendar space (Windows CalendarPage empty-area context menu).</summary>
public sealed partial class CalendarPageViewController
{
    private void SurfaceEmptyAreaContextRequested(object? sender, CalendarEmptyAreaContextRequestedEventArgs args)
    {
        CloseQuickEvent();

        var canPaste = ViewModel.HasCopiedCalendarItem && ViewModel.Readiness.IsReady;
        var target = args.Target;
        var hasTime = args.HasTime;
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.CalendarContextMenu_Paste, (_, _) => Observe(ViewModel.PasteCalendarItemAsync(target, hasTime)))
        {
            Enabled = canPaste
        });
        menu.PopUpMenu(null, args.Position, _surface);
    }
}
