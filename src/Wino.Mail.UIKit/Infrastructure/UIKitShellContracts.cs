using UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>Mail actions the shell routes to its content page (iPad hardware keyboard; the Mac toolbar and Message menu).</summary>
public enum ShellCommand
{
    Archive,
    Delete,
    Move,
    Flag,
    ToggleRead,
    Reply,
    ReplyAll,
    Forward
}

/// <summary>Implemented by the shell's current content page to receive shell commands (the Mac IShellCommandTarget).</summary>
public interface IShellCommandTarget
{
    bool CanExecute(ShellCommand command);

    /// <param name="anchor">The view that started the command when one exists, for popovers such as Move.</param>
    void Execute(ShellCommand command, UIView? anchor);

    /// <summary>Raised when selection or state changes so key commands revalidate.</summary>
    event EventHandler? CommandStateChanged;
}

/// <summary>Implemented by the shell's current content page to receive the toolbar search field (the Mac IShellSearchTarget).</summary>
public interface IShellSearchTarget
{
    Task SearchTextChangedAsync(string text);
    Task SearchSubmittedAsync(string text);
    Task SearchClearedAsync();

    /// <summary>The field's placeholder while this page is current; null keeps the generic "Search".</summary>
    string? SearchPlaceholder => null;
}

/// <summary>Keys a search suggestion target can take while the toolbar field edits (hardware keyboard).</summary>
public enum ShellSearchFieldCommand
{
    MoveUp,
    MoveDown,
    Submit,
    Cancel
}

/// <summary>
/// Optional companion to <see cref="IShellSearchTarget"/> for pages that show suggestions under the toolbar search field
/// (the Mac IShellSearchSuggestionTarget). Pages that do not implement it get the plain field behaviour.
/// </summary>
public interface IShellSearchSuggestionTarget
{
    /// <summary>Called before <see cref="IShellSearchTarget.SearchTextChangedAsync"/> with the field, so the page can anchor its list.</summary>
    void SearchFieldEditing(UISearchTextField field);

    /// <summary>A key while the field edits. Return true when handled.</summary>
    bool SearchFieldCommand(UISearchTextField field, ShellSearchFieldCommand command);

    /// <summary>The field stopped editing.</summary>
    void SearchFieldEndedEditing(UISearchTextField field);
}

/// <summary>
/// The reader frame as seen by a hosted page that can leave it (the Mac IReadingPaneHost): the iPad composer offers
/// "Open in new window" when <see cref="CanOpenInNewWindow"/> is true.
/// </summary>
public interface IReaderFrameHost
{
    /// <summary>True when a second window scene can be opened (iPad with multiple scenes enabled).</summary>
    bool CanOpenInNewWindow { get; }

    /// <summary>Moves <paramref name="child"/> out of the frame into its own window scene without releasing it; the frame empties.</summary>
    Task<bool> OpenInNewWindowAsync(UIViewController child);
}

/// <summary>Implemented by rendering-frame pages that need their host, such as the detachable composer.</summary>
public interface IReaderFrameChild
{
    IReaderFrameHost? FrameHost { get; set; }
}

/// <summary>
/// Shows the "Move to folder" popover anchored to a view (iPad). The reader command bar resolves it optionally and falls
/// back to its ViewModel command when it is missing or returns false (mixed accounts, no mail page).
/// </summary>
public interface IMailMovePresenter
{
    bool TryShowMove(IReadOnlyList<Wino.Core.Domain.Entities.Mail.MailCopy> mails, UIView anchor);
}

/// <summary>
/// A mode page that shows rendering-frame pages in its own area (the Mac IRenderingFrameHost). The page attaches itself
/// through <see cref="UIKitNavigationService.AttachRenderingHost"/> while it is the shell's content and detaches when it
/// is released; the router offers it every <see cref="UIKitPageHost.RenderingFrame"/> page before the shell's frame (the
/// iPad reader column, an iPhone push). The calendar page takes the event composer this way: in place over the surface
/// on iPad, as a full-screen sheet on iPhone. A host that takes a page owns it and releases it when it closes it or is
/// released itself; the router still activates it.
/// </summary>
public interface IRenderingFrameHost
{
    /// <summary>Shows <paramref name="page"/> in the host's own area. Main thread. False lets the shell's frame place it.</summary>
    bool TryShowInFrame(UIViewController page);
}
