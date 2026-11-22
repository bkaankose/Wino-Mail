using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// The contact drag in progress (macOS ContactDragPayload): the content provider carries a one-time token, the contact
/// ids stay in the process, so a string from another application never matches. Main thread only.
/// </summary>
internal static class ContactDragPayload
{
    public const string Prefix = "app.winomail.contacts:";

    private static Guid[] _current = [];
    private static string? _token;
    private static Action? _ended;
    private static MailDragCard? _card;
    private static string? _caption;
    private static bool _refused;

    /// <summary>Contact ids of the drag in progress; empty when nothing is dragged.</summary>
    public static IReadOnlyList<Guid> Current => _current;

    /// <summary>Starts a drag and returns the string the content provider carries. <paramref name="ended"/> runs once, at <see cref="End"/>.</summary>
    public static string Begin(Guid[] contactIds, Action? ended)
    {
        End();
        _current = contactIds;
        _ended = ended;
        _token = Prefix + Guid.NewGuid().ToString("N");
        return _token;
    }

    public static Guid[]? Resolve(string? text)
        => text is not null && _token is not null && string.Equals(text, _token, StringComparison.Ordinal) ? _current : null;

    /// <summary>Idempotent: a drop target ends the drag too, because the source may never report its end.</summary>
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

    /// <summary>What a contact list drop target reads (the Windows DataPackage properties).</summary>
    public static Dictionary<string, object> DataProperties(IReadOnlyList<Guid> contactIds)
        => new() { [ContactDragPackage.DataPropertyName] = new ContactDragPackage([.. contactIds]) };
}
