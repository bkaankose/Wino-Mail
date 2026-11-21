using Wino.Core.Domain;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's To, Cc and Bcc rows (macOS <c>MailRecipientRows</c>): a wrapping row that starts with its caption, then
/// one link per recipient. A row shows at most <see cref="TokenLimit"/> recipients and a "{0} more" link that expands it
/// until the next message.
/// </summary>
internal sealed class MailRecipientRows : IDisposable
{
    public const int To = 0, Cc = 1, Bcc = 2;
    public const int TokenLimit = 8;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Row[] _rows = new Row[3];
    private bool _disposed;

    private sealed class Row
    {
        public required Adw.WrapBox Flow { get; init; }
        public required Gtk.Label Caption { get; init; }
        public AccountContactViewModel[] Contacts { get; set; } = [];
        public bool Expanded { get; set; }
        public List<IDisposable> Subscriptions { get; } = [];
        public List<string> Tokens { get; } = [];
        public Gtk.Button? More { get; set; }
    }

    public MailRecipientRows()
    {
        for (var index = 0; index < _rows.Length; index++)
        {
            var flow = Adw.WrapBox.New();
            flow.SetChildSpacing(4);
            flow.SetLineSpacing(2);
            flow.SetVisible(false);
            var caption = Gtk.Label.New(string.Empty);
            caption.AddCssClass("caption-heading");
            caption.AddCssClass("dim-label");
            caption.SetValign(Gtk.Align.Center);
            _root.Append(flow);
            _rows[index] = new Row { Flow = flow, Caption = caption };
        }
        _root.SetVisible(false);
    }

    public Gtk.Widget Root => _root;

    public event Action<string, string, Gtk.Widget>? ContactInvoked;
    public event Action<string>? CopyRequested;

    /// <summary>True when the last call changed the tokens of the row (the owner closes an open contact card first).</summary>
    public bool SetRecipients(int row, string caption, AccountContactViewModel[] contacts)
    {
        var target = _rows[row];
        target.Caption.SetText(caption);
        var changed = !SameContacts(target.Contacts, contacts);
        if (changed)
        {
            target.Contacts = contacts;
            target.Expanded = false;
            Rebuild(target);
        }
        target.Flow.SetVisible(target.Contacts.Length > 0);
        _root.SetVisible(_rows.Any(static candidate => candidate.Contacts.Length > 0));
        return changed;
    }

    public void ResetExpansion()
    {
        foreach (var row in _rows)
        {
            if (!row.Expanded) continue;
            row.Expanded = false;
            Rebuild(row);
        }
    }

    public void Expand(int row)
    {
        if (_disposed || row is < 0 or > 2) return;
        var target = _rows[row];
        if (target.Expanded) return;
        target.Expanded = true;
        Rebuild(target);
    }

    public string Describe()
        => string.Join(" | ", _rows.Select(static row =>
            $"{row.Caption.GetText()} hidden={!row.Flow.GetVisible()} expanded={row.Expanded} count={row.Contacts.Length} tokens=[{string.Join(", ", row.Tokens)}]{(row.More is null ? string.Empty : " more")}"));

    public void Dispose()
    {
        _disposed = true;
        foreach (var row in _rows) Clear(row);
        ContactInvoked = null;
        CopyRequested = null;
    }

    private static bool SameContacts(AccountContactViewModel[] current, AccountContactViewModel[] next)
    {
        if (current.Length != next.Length) return false;
        for (var index = 0; index < current.Length; index++)
            if (!ReferenceEquals(current[index], next[index])) return false;
        return true;
    }

    private static void Clear(Row row)
    {
        foreach (var subscription in row.Subscriptions) subscription.Dispose();
        row.Subscriptions.Clear();
        row.Tokens.Clear();
        row.More = null;
        row.Flow.RemoveAll();
    }

    private void Rebuild(Row row)
    {
        Clear(row);
        row.Flow.Append(row.Caption);
        var shown = row.Expanded ? row.Contacts.Length : Math.Min(row.Contacts.Length, TokenLimit);
        for (var index = 0; index < shown; index++) row.Flow.Append(Token(row, row.Contacts[index]));
        var hidden = row.Contacts.Length - shown;
        if (hidden <= 0) return;
        var text = string.Format(Translator.MacOS_Reader_MoreRecipients, hidden);
        var more = Gtk.Button.NewWithLabel(text);
        more.AddCssClass("flat");
        more.AddCssClass(WinoCssClass.Recipient);
        more.AddCssClass("heading");
        WinoA11y.Rename(more, text);
        var rowIndex = Array.IndexOf(_rows, row);
        row.Subscriptions.Add(GtkSignals.OnClicked(more, () => MainLoop.Post(() => Expand(rowIndex))));
        row.More = more;
        row.Flow.Append(more);
    }

    private Gtk.Button Token(Row row, AccountContactViewModel contact)
    {
        var text = contact.ShortNameOrYouLabel ?? string.Empty;
        var label = Gtk.Label.New(text);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetMaxWidthChars(28);
        var token = Gtk.Button.New();
        token.SetChild(label);
        token.AddCssClass("flat");
        token.AddCssClass(WinoCssClass.Recipient);
        var display = contact.DisplayName ?? text;
        token.SetTooltipText(display);
        WinoA11y.Rename(token, display);
        var name = contact.Name ?? string.Empty;
        var address = contact.Address ?? string.Empty;
        row.Subscriptions.Add(GtkSignals.OnClicked(token, () => ContactInvoked?.Invoke(name, address, token)));
        row.Subscriptions.Add(ContextMenu.Attach(token, builder => builder.Item(Translator.MacOS_Reader_CopyAddress, () => CopyRequested?.Invoke(address), address.Length > 0)));
        row.Tokens.Add(text);
        return token;
    }
}
