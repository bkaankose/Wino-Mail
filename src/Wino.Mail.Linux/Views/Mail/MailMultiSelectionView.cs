using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

internal readonly record struct MailMultiSelectionSummary(int Count, int Unread, int Flagged, int WithAttachments, bool IsArchiveFolder, (string Name, string Address)[] Senders);

/// <summary>
/// The reading pane while several messages are selected (the macOS MailMultiSelectionView): up to three sender
/// pictures, the count, a summary line, the bulk actions and Unselect all. Flag and Mark read say what a click does for
/// the whole selection.
/// </summary>
internal sealed class MailMultiSelectionView : IDisposable
{
    private const int PictureSize = 40;

    private readonly LinuxSenderPictureProvider _pictures;
    private readonly Gtk.Box _avatars;
    private readonly WinoContactPicture[] _avatarViews;
    private readonly Gtk.Label _title;
    private readonly Gtk.Label _summary;
    private readonly Gtk.Button _archive;
    private readonly Gtk.Button _flag;
    private readonly Gtk.Button _read;
    private readonly List<IDisposable> _signals = [];
    private MailMultiSelectionSummary _shown;

    public MailMultiSelectionView(LinuxSenderPictureProvider pictures)
    {
        WinoLiveObjects.Track(this);
        _pictures = pictures;

        _avatars = Gtk.Box.New(Gtk.Orientation.Horizontal, -12);
        _avatars.SetHalign(Gtk.Align.Center);
        _avatarViews = new WinoContactPicture[3];
        for (var index = 0; index < _avatarViews.Length; index++)
        {
            _avatarViews[index] = new WinoContactPicture(PictureSize);
            _avatars.Append(_avatarViews[index].Root);
        }

        _title = Gtk.Label.New(string.Empty);
        _title.AddCssClass("title-2");
        _title.SetWrap(true);
        _title.SetJustify(Gtk.Justification.Center);
        _summary = Gtk.Label.New(string.Empty);
        _summary.AddCssClass("dim-label");
        _summary.SetWrap(true);
        _summary.SetJustify(Gtk.Justification.Center);

        _archive = Action(Translator.MailOperation_Archive, () => Raise(_shown.IsArchiveFolder ? MailOperation.UnArchive : MailOperation.Archive, _archive));
        var delete = Action(Translator.MailOperation_Delete, null);
        _signals.Add(GtkSignals.OnClicked(delete, () => Raise(MailOperation.SoftDelete, delete)));
        _flag = Action(Translator.MailOperation_Flag, () => Raise(AllFlagged ? MailOperation.ClearFlag : MailOperation.SetFlag, _flag));
        _read = Action(Translator.MailOperation_MarkAsRead, () => Raise(NoneUnread ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead, _read));
        var move = Action($"{Translator.MailOperation_Move}…", null);
        _signals.Add(GtkSignals.OnClicked(move, () => Raise(MailOperation.Move, move)));

        var grid = Gtk.Grid.New();
        grid.SetColumnSpacing(8);
        grid.SetRowSpacing(8);
        grid.SetColumnHomogeneous(true);
        grid.SetSizeRequest(240, -1);
        grid.SetHalign(Gtk.Align.Center);
        grid.Attach(_archive, 0, 0, 1, 1);
        grid.Attach(delete, 1, 0, 1, 1);
        grid.Attach(_flag, 0, 1, 1, 1);
        grid.Attach(_read, 1, 1, 1, 1);
        grid.Attach(move, 0, 2, 2, 1);

        var separator = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        separator.SetSizeRequest(240, -1);
        separator.SetHalign(Gtk.Align.Center);

        var unselect = Gtk.Button.NewWithLabel(Translator.Buttons_UnselectAll);
        unselect.AddCssClass("flat");
        unselect.AddCssClass("link");
        unselect.SetHalign(Gtk.Align.Center);
        _signals.Add(GtkSignals.OnClicked(unselect, () => UnselectAllRequested?.Invoke()));

        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 16);
        stack.SetValign(Gtk.Align.Center);
        stack.SetMarginStart(24);
        stack.SetMarginEnd(24);
        stack.Append(_avatars);
        stack.Append(_title);
        stack.Append(_summary);
        stack.Append(grid);
        stack.Append(separator);
        stack.Append(unselect);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(320);
        clamp.SetChild(stack);
        clamp.SetVexpand(true);
        clamp.SetValign(Gtk.Align.Center);
        Root = clamp;
    }

    public Gtk.Widget Root { get; }

    /// <summary>The operation and the clicked button (the anchor of a Move popover).</summary>
    public event Action<MailOperation, Gtk.Widget>? OperationRequested;

    public event Action? UnselectAllRequested;

    private bool AllFlagged => _shown.Count > 0 && _shown.Flagged == _shown.Count;

    private bool NoneUnread => _shown.Count > 0 && _shown.Unread == 0;

    public void Update(MailMultiSelectionSummary summary)
    {
        _shown = summary;
        for (var index = 0; index < _avatarViews.Length; index++)
        {
            var view = _avatarViews[index];
            if (index >= summary.Senders.Length)
            {
                view.Root.SetVisible(false);
                continue;
            }
            var (name, address) = summary.Senders[index];
            view.SetIdentity(string.IsNullOrWhiteSpace(name) ? address : name, string.IsNullOrWhiteSpace(address) ? name : address);
            view.Picture = _pictures.Get(address);
            view.Root.SetVisible(true);
        }

        var title = string.Format(Translator.MailsSelected, summary.Count);
        _title.SetText(title);
        WinoA11y.Label(Root, title);
        var parts = new List<string>(3);
        if (summary.Unread > 0) parts.Add($"{Translator.FilteringOption_Unread}: {summary.Unread}");
        if (summary.Flagged > 0) parts.Add($"{Translator.FilteringOption_Flagged}: {summary.Flagged}");
        if (summary.WithAttachments > 0) parts.Add($"{Translator.FilteringOption_Files}: {summary.WithAttachments}");
        _summary.SetText(string.Join(" · ", parts));
        _summary.SetVisible(parts.Count > 0);
        _flag.SetLabel(AllFlagged ? Translator.MailOperation_ClearFlag : Translator.MailOperation_Flag);
        _read.SetLabel(NoneUnread ? Translator.MailOperation_MarkAsUnread : Translator.MailOperation_MarkAsRead);
    }

    /// <summary>The visible texts, for the Debug state report.</summary>
    public string Describe() => $"{_title.GetText()} | {_summary.GetText()} | {_archive.GetLabel()}, {_flag.GetLabel()}, {_read.GetLabel()}";

    public void Dispose()
    {
        foreach (var signal in _signals) signal.Dispose();
        _signals.Clear();
        OperationRequested = null;
        UnselectAllRequested = null;
    }

    private void Raise(MailOperation operation, Gtk.Widget anchor) => OperationRequested?.Invoke(operation, anchor);

    private Gtk.Button Action(string title, Action? clicked)
    {
        var button = Gtk.Button.NewWithLabel(title);
        button.AddCssClass("pill");
        if (clicked is not null) _signals.Add(GtkSignals.OnClicked(button, clicked));
        return button;
    }
}
