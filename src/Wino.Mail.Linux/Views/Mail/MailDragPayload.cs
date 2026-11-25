using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell.Contracts;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The mail drag in progress (macOS <c>MailDragPayload</c>): the content provider carries an opaque token, the mails
/// stay in process. A string from another application never matches the token. Main thread only.
/// </summary>
internal static class MailDragPayload
{
    public const string Prefix = "app.winomail.mails:";

    private static MailCopy[] _current = [];
    private static string? _token;
    private static Action? _ended;
    private static MailDragCard? _card;
    private static string? _caption;
    private static bool _refused;

    /// <summary>Mails of the drag in progress; empty when nothing is dragged.</summary>
    public static IReadOnlyList<MailCopy> Current => _current;

    /// <summary>Starts a drag and returns the string the content provider carries. <paramref name="ended"/> runs once, at <see cref="End"/>.</summary>
    public static string Begin(MailCopy[] mails, Action? ended)
    {
        End();
        _current = mails;
        _ended = ended;
        _token = Prefix + Guid.NewGuid().ToString("N");
        return _token;
    }

    public static MailCopy[]? Resolve(string? text) => text is not null && _token is not null && string.Equals(text, _token, StringComparison.Ordinal) ? _current : null;

    public static bool IsMailDrag(string? text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Idempotent. A drop that moves the mails removes their rows, and the source may then never report the end of its
    /// drag, so the drop target ends the drag too.
    /// </summary>
    public static void End()
    {
        _current = [];
        _token = null;
        _card = null;
        _caption = null;
        _refused = false;
        var ended = _ended;
        _ended = null;
        ended?.Invoke();
    }

    /// <summary>The live drag icon, for captions.</summary>
    public static void SetCard(MailDragCard? card)
    {
        _card = card;
        _caption = null;
        _refused = false;
    }

    public static void SetCaption(string? caption, bool refused)
    {
        if (string.Equals(caption, _caption, StringComparison.Ordinal) && refused == _refused) return;
        _caption = caption;
        _refused = refused;
        _card?.SetCaption(caption, refused);
    }

    /// <summary>
    /// Windows <c>CanContinueDragDrop</c>: the folder is a move target, not the selected folder, and the shared drop
    /// rules (<see cref="IMailShellClient.GetMailsToMove"/>) move at least one dragged mail there.
    /// </summary>
    public static bool CanDropOn(IBaseFolderMenuItem folder, bool isSelectedFolder, IReadOnlyList<MailCopy> mails, IMailShellClient? mail)
        => mail is not null && mails.Count > 0 && !isSelectedFolder && folder.IsMoveTarget && mail.GetMailsToMove(folder, mails).Count > 0;

    /// <summary>Windows <c>ItemDragOverAccount</c>: the shared drop rules move at least one dragged mail into another account.</summary>
    public static bool CanDropOn(IAccountMenuItem account, IReadOnlyList<MailCopy> mails, IMailShellClient? mail)
        => mail is not null && mails.Count > 0 && mail.GetMailsToMoveToAccount(account, mails).Count > 0;
}
