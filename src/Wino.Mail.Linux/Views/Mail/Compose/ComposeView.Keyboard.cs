using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Shell;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The composer's fixed keys: F6 and Enter land in the first empty field (To, Subject, then the body), Ctrl+Shift+A
/// attaches files while the focus is inside the composer, and Ctrl+O pops a docked composer out.
/// </summary>
internal sealed partial class ComposeView : IPaneFocus, IKeyActionTarget
{
    public bool FocusPrimary()
    {
        if (IsReleased || !Root.GetMapped()) return false;
        if (ViewModel.ToItems.Count == 0 && _recipients.To.ChipCount == 0)
        {
            _recipients.To.FocusEntry();
            return true;
        }
        if (string.IsNullOrWhiteSpace(_subject.GetText())) return _subject.GrabFocus();
        if (Editor is not { } editor || _editorDisposed) return _subject.GrabFocus();
        Observe(editor.FocusEditorAsync(true));
        return true;
    }

    public bool TryHandleKeyAction(string action)
    {
        if (IsReleased) return false;
        switch (action)
        {
            case LinuxKeymap.ComposeAttach:
                if (Root.GetRoot() is not Gtk.Window window || !ShellView.Contains(Root, window.GetFocus())) return false;
                AttachFiles();
                return true;
            case LinuxKeymap.OpenInWindow:
                if (_window is not null || !SupportsPopOut) return false;
                Observe(PopOutAsync());
                return true;
            default:
                return false;
        }
    }

    /// <summary>The keys a popped-out composer runs itself.</summary>
    internal bool TryHandlePopOutKey(string action) => action == LinuxKeymap.ComposeAttach && TryHandleKeyAction(action);
}
