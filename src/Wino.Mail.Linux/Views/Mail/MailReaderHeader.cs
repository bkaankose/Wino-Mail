using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

internal readonly record struct ReaderCategoryChip(string Name, string? BackgroundHex, string? TextHex);

/// <summary>
/// The reader's sender block (macOS layout): contact picture | the sender link and the To, Cc and Bcc rows | the date,
/// Unsubscribe and the S/MIME indicators; then the category chips.
/// </summary>
internal sealed class MailReaderHeader : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
    private readonly WinoContactPicture _picture = new(WinoContactPicture.ReaderSize);
    private readonly Gtk.Button _sender = Gtk.Button.New();
    private readonly Gtk.Label _senderName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _senderAddress = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _date = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _status = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.Button _unsubscribe = Gtk.Button.New();
    private readonly Gtk.Overlay _signature = Gtk.Overlay.New();
    private readonly Adw.Bin _signatureDot = Adw.Bin.New();
    private readonly Gtk.Box _encrypted;
    private readonly Adw.WrapBox _chips = Adw.WrapBox.New();
    private readonly List<IDisposable> _subscriptions = [];
    private ReaderCategoryChip[] _chipValues = [];
    private string _senderText = string.Empty;
    private string _addressText = string.Empty;
    private bool _compact;
    private bool _canUnsubscribe, _signed, _valid, _isEncrypted;

    public MailReaderHeader()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass(WinoCssClass.ReaderHeader);
        Recipients = new MailRecipientRows();

        _senderName.AddCssClass("heading");
        _senderName.SetEllipsize(Pango.EllipsizeMode.End);
        _senderName.SetXalign(0);
        _senderAddress.AddCssClass("dim-label");
        _senderAddress.SetEllipsize(Pango.EllipsizeMode.End);
        _senderAddress.SetXalign(0);
        var senderLine = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        senderLine.Append(_senderName);
        senderLine.Append(_senderAddress);
        _sender.SetChild(senderLine);
        _sender.AddCssClass("flat");
        _sender.AddCssClass(WinoCssClass.ReaderSender);
        _sender.SetHalign(Gtk.Align.Start);
        _subscriptions.Add(GtkSignals.OnClicked(_sender, () => SenderInvoked?.Invoke(_sender)));
        _subscriptions.Add(ContextMenu.Attach(_sender, builder => builder.Item(Translator.MacOS_Reader_CopyAddress, () => SenderCopyRequested?.Invoke(), _addressText.Length > 0)));

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 3);
        text.SetHexpand(true);
        text.Append(_sender);
        text.Append(Recipients.Root);

        _date.AddCssClass("dim-label");
        _date.AddCssClass("numeric");
        _date.AddCssClass("caption");
        _date.SetEllipsize(Pango.EllipsizeMode.End);
        _date.SetWidthChars(12);
        DateLine = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        DateLine.SetHalign(Gtk.Align.End);
        DateLine.Append(_date);

        _unsubscribe.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.PersonDelete, Translator.Unsubscribe, 14));
        _unsubscribe.AddCssClass("flat");
        _unsubscribe.AddCssClass("accent");
        WinoA11y.Rename(_unsubscribe, Translator.Unsubscribe);
        _subscriptions.Add(GtkSignals.OnClicked(_unsubscribe, () => UnsubscribeInvoked?.Invoke()));
        var signatureButton = WinoIcons.Button(WinoIconGlyph.Certificate, Translator.SmimeSignedTooltip);
        _subscriptions.Add(GtkSignals.OnClicked(signatureButton, () => SignatureInvoked?.Invoke()));
        _signatureDot.AddCssClass(WinoCssClass.StatusDot);
        _signatureDot.SetHalign(Gtk.Align.End);
        _signatureDot.SetValign(Gtk.Align.Start);
        _signatureDot.SetCanTarget(false);
        _signature.SetChild(signatureButton);
        _signature.AddOverlay(_signatureDot);
        _encrypted = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _encrypted.AccessibleRole = Gtk.AccessibleRole.Img;
        WinoA11y.LabelWithTooltip(_encrypted, Translator.SmimeEncryptedTooltip);
        _encrypted.SetValign(Gtk.Align.Center);
        _encrypted.Append(WinoIcons.Label(WinoIconGlyph.LockClosed, 16));
        _status.SetHalign(Gtk.Align.End);
        _status.Append(_unsubscribe);
        _status.Append(_signature);
        _status.Append(_encrypted);
        SetStatus(false, false, false, false);

        var details = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        details.SetHalign(Gtk.Align.End);
        details.Append(DateLine);
        details.Append(_status);

        var senderRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        senderRow.SetValign(Gtk.Align.Start);
        _picture.Root.SetValign(Gtk.Align.Start);
        senderRow.Append(_picture.Root);
        senderRow.Append(text);
        senderRow.Append(details);

        _chips.SetChildSpacing(8);
        _chips.SetLineSpacing(4);
        _chips.SetVisible(false);

        _root.Append(senderRow);
        _root.Append(_chips);
    }

    public Gtk.Widget Root => _root;
    public Gtk.Box DateLine { get; }
    public Gtk.Widget SenderAnchor => _sender;
    public MailRecipientRows Recipients { get; }

    public event Action<Gtk.Widget>? SenderInvoked;
    public event Action? SenderCopyRequested;
    public event Action? UnsubscribeInvoked;
    public event Action? SignatureInvoked;

    public void SetSender(string? name, string? address)
    {
        address ??= string.Empty;
        var display = string.IsNullOrWhiteSpace(name) ? address : name;
        var showAddress = address.Length > 0 && !string.Equals(display, address, StringComparison.OrdinalIgnoreCase);
        _senderText = display;
        _addressText = address;
        _senderName.SetText(display);
        _senderAddress.SetText(showAddress ? address : string.Empty);
        _senderAddress.SetVisible(showAddress);
        var from = Translator.ComposerFrom.Trim();
        WinoA11y.Rename(_sender, showAddress ? $"{from} {display} <{address}>" : $"{from} {display}");
        _picture.SetIdentity(display, address);
    }

    public void SetSenderPicture(Gdk.Texture? picture) => _picture.Picture = picture;

    public void SetDate(string text) => _date.SetText(text);

    public void SetStatus(bool canUnsubscribe, bool isSigned, bool signaturesValid, bool isEncrypted)
    {
        (_canUnsubscribe, _signed, _valid, _isEncrypted) = (canUnsubscribe, isSigned, signaturesValid, isEncrypted);
        _unsubscribe.SetVisible(canUnsubscribe);
        _signature.SetVisible(isSigned);
        _signatureDot.RemoveCssClass(signaturesValid ? "warning" : "success");
        _signatureDot.AddCssClass(signaturesValid ? "success" : "warning");
        _encrypted.SetVisible(isEncrypted);
        _status.SetVisible(canUnsubscribe || isSigned || isEncrypted);
    }

    public void SetCategories(ReaderCategoryChip[] chips)
    {
        if (chips.AsSpan().SequenceEqual(_chipValues)) return;
        _chipValues = chips;
        _chips.RemoveAll();
        foreach (var value in chips)
        {
            var chip = new WinoChip(22) { Text = value.Name };
            chip.SetColors(value.BackgroundHex, value.TextHex);
            _chips.Append(chip.Root);
        }
        _chips.SetVisible(chips.Length > 0);
    }

    public void SetCompact(bool compact)
    {
        _compact = compact;
        _picture.Root.SetVisible(!compact);
    }

    public string Describe()
        => $"sender=\"{_senderText}\" address=\"{_addressText}\" picture={_picture.Picture is not null} date=\"{_date.GetText()}\" unsubscribe={_canUnsubscribe} signed={_signed} valid={_valid} encrypted={_isEncrypted} chips={_chipValues.Length} compact={_compact}";

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Recipients.Dispose();
        SenderInvoked = null;
        SenderCopyRequested = null;
        UnsubscribeInvoked = null;
        SignatureInvoked = null;
    }
}
