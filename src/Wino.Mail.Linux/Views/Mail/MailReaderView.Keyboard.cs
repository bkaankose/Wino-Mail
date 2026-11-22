using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Core.Domain.Models.Menus;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's fixed keys: F6 and Enter land in the body, Ctrl+P prints, Ctrl+plus, Ctrl+minus and Ctrl+0 zoom the body
/// (for this reader's lifetime only), Ctrl+O pops the reader out and Ctrl+J marks the message as junk. The body is marked
/// read-only so the router lets letters and Delete reach the shortcuts while copy gestures stay with the web view.
/// </summary>
internal sealed partial class MailReaderView : IPaneFocus, IKeyActionTarget
{
    private const double ZoomStep = 0.1;
    private const double ZoomMinimum = 0.5;
    private const double ZoomMaximum = 3.0;

    partial void InitializeKeyboard()
    {
        if (_reader is not { } reader) return;
        reader.View.AddCssClass(ShortcutRouter.ReadOnlyWebClass);
#if DEBUG
        WhenDescribing(() => $"zoom={reader.View.GetZoomLevel():0.0#}");
#endif
    }

    public bool FocusPrimary()
    {
        if (IsReleased || _reader is not { } reader || !reader.View.GetMapped()) return false;
        _subject.SelectRegion(0, 0);
        return reader.View.GrabFocus();
    }

    public bool TryHandleKeyAction(string action)
    {
        if (IsReleased) return false;
        switch (action)
        {
            case LinuxKeymap.Print:
                if (_currentItem is null || _currentHtml.Length == 0) return false;
                RunOperation(MailOperationMenuItem.Create(MailOperation.Print));
                return true;
            case LinuxKeymap.ZoomIn: return Zoom(ZoomStep);
            case LinuxKeymap.ZoomOut: return Zoom(-ZoomStep);
            case LinuxKeymap.ZoomReset: return Zoom(0);
            case LinuxKeymap.OpenInWindow: return PopOut();
            case LinuxKeymap.Junk:
                if (_currentItem is not { } item) return false;
                RunOperation(MailOperationMenuItem.Create(item.MailCopy.AssignedFolder?.SpecialFolderType == SpecialFolderType.Junk ? MailOperation.MarkAsNotJunk : MailOperation.MoveToJunk));
                return true;
            default:
                return false;
        }
    }

    /// <summary>The keys a popped-out reader runs itself: print and zoom.</summary>
    internal bool TryHandlePopOutKey(string action)
        => action is LinuxKeymap.Print or LinuxKeymap.ZoomIn or LinuxKeymap.ZoomOut or LinuxKeymap.ZoomReset && TryHandleKeyAction(action);

    private bool Zoom(double step)
    {
        if (_reader is not { } reader) return false;
        var level = step == 0 ? 1.0 : Math.Clamp(Math.Round(reader.View.GetZoomLevel() + step, 1), ZoomMinimum, ZoomMaximum);
        reader.View.SetZoomLevel(level);
        return true;
    }
}
