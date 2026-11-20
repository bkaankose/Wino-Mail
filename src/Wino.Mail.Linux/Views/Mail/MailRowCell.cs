using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>A recycled list cell: either a group header or a mail row. It only renders the shared row description.</summary>
internal sealed class MailRowCell
{
    private readonly Gtk.Label _header = Label("wino-group-header");
    private readonly Gtk.Box _mail = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Image _unread = Gtk.Image.NewFromIconName("media-record-symbolic");
    private readonly Gtk.Label _sender = Label(null);
    private readonly Gtk.Label _date = Label("caption");
    private readonly Gtk.Label _subject = Label(null);
    private readonly Gtk.Label _preview = Label("caption");
    private readonly Gtk.Image _attachment = Gtk.Image.NewFromIconName("mail-attachment-symbolic");
    private readonly Gtk.Image _flag = Gtk.Image.NewFromIconName("starred-symbolic");
    private readonly List<INotifyPropertyChanged> _observed = [];
    private MailListRow? _row;
    private TimeFormatPreference _timeFormat;
    private bool _refreshQueued;

    public MailRowCell()
    {
        _unread.SetPixelSize(10);
        _unread.SetValign(Gtk.Align.Start);
        _unread.SetMarginTop(6);
        _unread.AddCssClass("wino-unread-dot");
        _sender.SetHexpand(true);
        _date.AddCssClass("dim-label");
        _date.AddCssClass("numeric");
        _preview.AddCssClass("dim-label");
        _attachment.SetPixelSize(12);
        _flag.SetPixelSize(12);

        var first = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        first.Append(_sender);
        first.Append(_attachment);
        first.Append(_flag);
        first.Append(_date);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetHexpand(true);
        text.Append(first);
        text.Append(_subject);
        text.Append(_preview);
        _mail.AddCssClass("wino-mail-row");
        _mail.Append(_unread);
        _mail.Append(text);

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(_header);
        root.Append(_mail);
        Root = root;
    }

    public Gtk.Widget Root { get; }

    public void Bind(string? header, MailListRow? row, TimeFormatPreference timeFormat)
    {
        Unbind();
        _timeFormat = timeFormat;
        _header.SetVisible(header is not null);
        _mail.SetVisible(row is not null);
        if (header is not null) _header.SetText(header);
        if (row is null) return;

        _row = row;
        // A row shows state of its mails (read, flagged), which changes without the projection changing.
        foreach (var leaf in MailRowPresentation.Leaves(row))
        {
            leaf.PropertyChanged += LeafChanged;
            _observed.Add(leaf);
        }
        Apply();
    }

    public void Unbind()
    {
        foreach (var leaf in _observed) leaf.PropertyChanged -= LeafChanged;
        _observed.Clear();
        _row = null;
    }

    private void LeafChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _refreshQueued = false;
            Apply();
            return false;
        });
    }

    private void Apply()
    {
        if (_row is null || MailRowPresentation.Describe(_row, _timeFormat) is not { } content) return;
        var sender = content.ThreadCount > 1 ? $"{content.Sender}  ({content.ThreadCount})" : content.Sender;
        _sender.SetText(content.IsDraft ? $"[{Translator.Draft}] {sender}" : sender);
        _subject.SetText(content.Subject);
        _preview.SetText(content.Preview);
        _date.SetText(content.DateText);
        _unread.SetOpacity(content.IsUnread ? 1 : 0);
        _attachment.SetVisible(content.HasAttachments);
        _flag.SetVisible(content.IsFlagged);
        Toggle(_sender, content.IsUnread);
        Toggle(_subject, content.IsUnread);
    }

    private static void Toggle(Gtk.Widget widget, bool unread)
    {
        if (unread) widget.AddCssClass("wino-unread");
        else widget.RemoveCssClass("wino-unread");
    }

    private static Gtk.Label Label(string? cssClass)
    {
        var label = Gtk.Label.New(string.Empty);
        label.SetXalign(0);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetSingleLineMode(true);
        if (cssClass is not null) label.AddCssClass(cssClass);
        return label;
    }
}
