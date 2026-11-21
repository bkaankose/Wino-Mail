using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.MailRow;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>
/// The Wino mail row, laid out like the Windows MailListItemTemplates and the AppKit WinoMailRowView: a highlight box
/// with the selection pill, the avatar, then three lines. Line 1: nickname, draft tag, sender, indicators. Line 2:
/// thread chevron, subject, date and unread dot. Line 3: the preview (not in Compact). Density padding comes from the
/// density class on the list. <see cref="Apply"/> touches only what changed, so recycled cells stay cheap.
/// </summary>
public sealed class WinoMailRow : IDisposable
{
    private readonly Gtk.Overlay _root;
    private readonly Gtk.Box _pill;
    private readonly Gtk.CheckButton _check;
    private readonly WinoContactPicture _avatar;
    private readonly Gtk.Label _nicknameLeft;
    private readonly Gtk.Label _draftTag;
    private readonly Gtk.Label _sender;
    private readonly Gtk.Label _nicknameRight;
    private readonly Gtk.Label _pin;
    private readonly Gtk.Label _attachment;
    private readonly Gtk.Label _flag;
    private readonly Adw.Spinner _busy;
    private readonly Gtk.Button _chevron;
    private readonly Gtk.Label _chevronGlyph;
    private readonly Gtk.Label _subject;
    private readonly Gtk.Label _date;
    private readonly Gtk.Box _unreadDot;
    private readonly Gtk.Label _preview;
    private readonly IDisposable _chevronClicked;
    private readonly IDisposable _checkToggled;
    private WinoMailRowModel _model = new();
    private WinoTileLine? _tiles;
    private string? _leftTint;
    private string? _rightTint;
    private string _accessibleText = string.Empty;
    private string _chevronName = string.Empty;
    private bool _first = true;
    private bool _applyingCheck;
    private bool _selectionMode;
    private bool _disposed;

    public WinoMailRow()
    {
        WinoLiveObjects.Track(this);

        _root = Gtk.Overlay.New();
        _root.SetHexpand(true);

        ContentBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        ContentBox.AddCssClass(WinoCssClass.MailRow);
        _root.SetChild(ContentBox);

        _pill = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _pill.AddCssClass(WinoCssClass.MailRowPill);
        _pill.SetValign(Gtk.Align.Center);
        _pill.SetCanTarget(false);

        _check = Gtk.CheckButton.New();
        _check.SetVisible(false);
        _check.SetValign(Gtk.Align.Center);
        _check.SetFocusable(false);
        _check.SetMarginEnd((int)WinoMailRowMetrics.CheckboxSpacing);

        _avatar = new WinoContactPicture((int)WinoMailRowMetrics.AvatarSize(WinoMailRowDensity.Medium));
        _avatar.Root.SetMarginEnd((int)WinoMailRowMetrics.AvatarSpacing);
        _avatar.Root.SetValign(Gtk.Align.Center);
        _avatar.Root.SetCanTarget(false);

        _nicknameLeft = Text(WinoCssClass.MailNickname);
        _nicknameLeft.SetVisible(false);
        _nicknameLeft.SetHexpand(false);
        _draftTag = Text(WinoCssClass.MailDraft);
        _draftTag.SetText("[" + Translator.Draft + "]");
        _draftTag.SetVisible(false);
        _draftTag.SetEllipsize(Pango.EllipsizeMode.None);
        _draftTag.SetMaxWidthChars(-1);
        _sender = Text(WinoCssClass.MailSender);
        _sender.SetHexpand(true);
        _nicknameRight = Text(WinoCssClass.MailNickname);
        _nicknameRight.SetVisible(false);

        CompactTileSlot = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        CompactTileSlot.SetVisible(false);
        _pin = Glyph(WinoIconGlyph.Pin, 12, "dim-label");
        _pin.SetTooltipText(Translator.SearchBar_Pinned);
        _attachment = Glyph(WinoIconGlyph.Attachment, 14, WinoCssClass.MailAttachment);
        _flag = Glyph(WinoIconGlyph.Flag, 14, WinoCssClass.MailFlag);
        _busy = Adw.Spinner.New();
        _busy.SetSizeRequest(12, 12);
        _busy.SetVisible(false);
        _busy.SetCanTarget(false);

        var indicators = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        indicators.SetMarginStart(4);
        indicators.Append(CompactTileSlot);
        indicators.Append(_pin);
        indicators.Append(_attachment);
        indicators.Append(_flag);
        indicators.Append(_busy);

        var line1 = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        line1.Append(_nicknameLeft);
        line1.Append(_draftTag);
        line1.Append(_sender);
        line1.Append(_nicknameRight);
        line1.Append(indicators);

        _chevronGlyph = WinoIcons.Label(WinoIconGlyph.ChevronRight, 10);
        _chevron = Gtk.Button.New();
        _chevron.AddCssClass("flat");
        _chevron.AddCssClass(WinoCssClass.MailChevron);
        _chevron.SetChild(_chevronGlyph);
        _chevron.SetFocusable(false);
        _chevron.SetValign(Gtk.Align.Center);
        _chevron.SetVisible(false);

        _subject = Text(WinoCssClass.MailSubject);
        _subject.SetHexpand(true);
        _date = Text("caption");
        _date.AddCssClass("dim-label");
        _date.AddCssClass("numeric");
        _date.SetEllipsize(Pango.EllipsizeMode.None);
        _date.SetMaxWidthChars(-1);
        _unreadDot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _unreadDot.AddCssClass(WinoCssClass.UnreadDot);
        _unreadDot.SetValign(Gtk.Align.Center);
        _unreadDot.SetVisible(false);
        _unreadDot.SetCanTarget(false);

        var subjectLine = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        subjectLine.SetHexpand(true);
        subjectLine.Append(_chevron);
        subjectLine.Append(_subject);
        var line2 = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        line2.Append(subjectLine);
        line2.Append(_date);
        line2.Append(_unreadDot);

        _preview = Text("caption");
        _preview.AddCssClass("dim-label");
        _preview.AddCssClass(WinoCssClass.MailPreview);

        TileSlot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        TileSlot.SetVisible(false);

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, (int)WinoMailRowMetrics.LineSpacing);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(line1);
        text.Append(line2);
        text.Append(_preview);
        text.Append(TileSlot);

        ContentBox.Append(_pill);
        ContentBox.Append(_check);
        ContentBox.Append(_avatar.Root);
        ContentBox.Append(text);

        _chevronClicked = GtkSignals.OnClicked(_chevron, () => ThreadToggleRequested?.Invoke(this));
        _checkToggled = GtkSignals.OnToggled(_check, () =>
        {
            if (!_applyingCheck) CheckToggled?.Invoke(this);
        });
    }

    /// <summary>A <see cref="Gtk.Overlay"/> whose child is <see cref="ContentBox"/>.</summary>
    public Gtk.Widget Root => _root;

    /// <summary>The highlight box (class <c>wino-mail-row</c>).</summary>
    public Gtk.Box ContentBox { get; }

    public WinoMailRowModel Model => _model;

    /// <summary>Line 1, before the indicators; hidden until a later package fills it.</summary>
    public Gtk.Box CompactTileSlot { get; }

    /// <summary>Under the preview; hidden until a later package fills it.</summary>
    public Gtk.Box TileSlot { get; }

    public event Action<WinoMailRow>? CheckToggled;

    public event Action<WinoMailRow>? ThreadToggleRequested;

    public bool IsSelectionMode
    {
        get => _selectionMode;
        set
        {
            if (_selectionMode == value) return;
            _selectionMode = value;
            _check.SetVisible(value);
        }
    }

    /// <summary>Sets the check box without raising <see cref="CheckToggled"/>.</summary>
    public bool IsChecked
    {
        get => _check.GetActive();
        set
        {
            if (_check.GetActive() == value) return;
            _applyingCheck = true;
            try { _check.SetActive(value); }
            finally { _applyingCheck = false; }
        }
    }

    public Gtk.CheckButton CheckBox => _check;

    public Gtk.Button Chevron => _chevron;

    /// <summary>Null until the row first showed a tile.</summary>
    public WinoTileLine? Tiles => _tiles;

    public void AddOverlay(Gtk.Widget widget) => _root.AddOverlay(widget);

    public void Apply(WinoMailRowModel model)
    {
        if (_disposed) return;
        var previous = _model;
        var all = _first;
        _first = false;
        _model = model;

        if (all || previous.Density != model.Density) _avatar.Size = (int)WinoMailRowMetrics.AvatarSize(model.Density);
        if (all || previous.Kind != model.Kind) Toggle(ContentBox, WinoCssClass.ThreadChild, model.Kind == WinoMailRowKind.ThreadChild);

        if (all || previous.ShowPicture != model.ShowPicture) _avatar.Root.SetVisible(model.ShowPicture);
        if (all || previous.Sender != model.Sender || previous.SenderAddress != model.SenderAddress)
            _avatar.SetIdentity(Blank(model.Sender) ? model.SenderAddress : model.Sender, Blank(model.SenderAddress) ? model.Sender : model.SenderAddress);
        _avatar.Picture = model.ShowPicture ? model.Picture : null;

        var sender = SenderText(model);
        if (all || !string.Equals(SenderText(previous), sender, StringComparison.Ordinal)) _sender.SetText(sender);
        if (all || previous.IsDraft != model.IsDraft) _draftTag.SetVisible(model.IsDraft);

        if (all || previous.ShowNickname != model.ShowNickname || previous.NicknamePosition != model.NicknamePosition
            || previous.AccountNickname != model.AccountNickname || previous.AccountColorHex != model.AccountColorHex)
        {
            Nickname(_nicknameLeft, ref _leftTint, model, AccountNicknamePosition.Left);
            Nickname(_nicknameRight, ref _rightTint, model, AccountNicknamePosition.Right);
        }

        if (all || previous.IsPinned != model.IsPinned) _pin.SetVisible(model.IsPinned);
        if (all || previous.HasAttachments != model.HasAttachments) _attachment.SetVisible(model.HasAttachments);
        if (all || previous.IsFlagged != model.IsFlagged) _flag.SetVisible(model.IsFlagged);
        if (all || previous.IsBusy != model.IsBusy) _busy.SetVisible(model.IsBusy);

        if (all || previous.ShowChevron != model.ShowChevron) _chevron.SetVisible(model.ShowChevron);
        if (model.ShowChevron && (all || previous.IsThreadExpanded != model.IsThreadExpanded || !previous.ShowChevron))
            WinoIcons.SetGlyph(_chevronGlyph, WinoIcons.Glyph(model.IsThreadExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight));
        if (model.ShowChevron)
        {
            var name = $"{model.ThreadCount}, {(model.IsThreadExpanded ? Translator.Accessibility_MailThreadExpanded : Translator.Accessibility_MailThreadCollapsed)}";
            if (!string.Equals(name, _chevronName, StringComparison.Ordinal))
            {
                _chevronName = name;
                WinoA11y.Label(_chevron, name);
            }
        }

        if (all || previous.Subject != model.Subject) _subject.SetText(model.Subject);
        if (all || previous.DateText != model.DateText) _date.SetText(model.DateText);
        if (all || previous.IsUnread != model.IsUnread)
        {
            Toggle(_sender, WinoCssClass.Unread, model.IsUnread);
            Toggle(_subject, WinoCssClass.Unread, model.IsUnread);
            _unreadDot.SetVisible(model.IsUnread);
        }

        var showPreview = model.Density != WinoMailRowDensity.Compact && model.ShowPreview && !Blank(model.Preview);
        if (all || previous.Preview != model.Preview) _preview.SetText(OneLine(model.Preview));
        _preview.SetVisible(showPreview);

        if (model.Tiles.Length > 0 || _tiles is not null)
            (_tiles ??= new WinoTileLine(TileSlot, CompactTileSlot)).Apply(model.Tiles, model.Density);

        if (!string.Equals(model.AccessibilityText, _accessibleText, StringComparison.Ordinal))
        {
            _accessibleText = model.AccessibilityText;
            WinoA11y.Label(_root, model.AccessibilityText);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chevronClicked.Dispose();
        _checkToggled.Dispose();
        CheckToggled = null;
        ThreadToggleRequested = null;
    }

    private static void Nickname(Gtk.Label label, ref string? tint, WinoMailRowModel model, AccountNicknamePosition side)
    {
        var visible = model.ShowNickname && model.NicknamePosition == side;
        label.SetVisible(visible);
        if (!visible) return;
        label.SetText(model.AccountNickname ?? string.Empty);
        WinoTint.Apply(label, ref tint, model.AccountColorHex);
    }

    private static string SenderText(WinoMailRowModel model) => model.IsDraft && Blank(model.Sender) ? model.SenderAddress : model.Sender;

    private static string OneLine(string text)
        => text.Contains('\n') || text.Contains('\r') ? text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') : text;

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static void Toggle(Gtk.Widget widget, string cssClass, bool on)
    {
        if (on) widget.AddCssClass(cssClass);
        else widget.RemoveCssClass(cssClass);
    }

    private static Gtk.Label Glyph(WinoIconGlyph glyph, int size, string cssClass)
    {
        var label = WinoIcons.Label(glyph, size);
        label.AddCssClass(cssClass);
        label.SetVisible(false);
        label.SetCanTarget(false);
        label.SetValign(Gtk.Align.Center);
        return label;
    }

    // An ellipsised label asks for its whole text as natural width; the cap keeps long subjects from widening the pane.
    private static Gtk.Label Text(string? cssClass)
    {
        var label = Gtk.Label.New(string.Empty);
        label.SetXalign(0);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetSingleLineMode(true);
        label.SetMaxWidthChars(8);
        if (cssClass is not null) label.AddCssClass(cssClass);
        return label;
    }
}
