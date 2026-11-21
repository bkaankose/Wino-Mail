using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The contact card of the reader (Windows InternetAddressTemplate flyout, macOS contact card): the contact picture,
/// the name and the address, which copies when clicked. One popover per <see cref="Show"/>.
/// </summary>
internal sealed class ContactCardPopover : IDisposable
{
    private readonly Func<string?, Gdk.Texture?> _picture;
    private Gtk.Popover? _popover;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public ContactCardPopover(Func<string?, Gdk.Texture?> picture)
    {
        _picture = picture;
        WinoLiveObjects.Track(this);
    }

    public event Action<string>? CopyRequested;

    public bool IsShown => _popover?.GetVisible() == true;

    public void Show(Gtk.Widget anchor, string? name, string address)
    {
        if (_disposed || string.IsNullOrWhiteSpace(address) || anchor.GetRoot() is null) return;
        if (_popover is { } previous)
        {
            previous.Popdown();
            Teardown(previous);
        }

        var display = string.IsNullOrWhiteSpace(name) ? address : name;
        var picture = new WinoContactPicture(36);
        picture.SetIdentity(display, address);
        picture.Picture = _picture(address);
        picture.Root.SetValign(Gtk.Align.Start);

        var nameLabel = Gtk.Label.New(display);
        nameLabel.AddCssClass("heading");
        nameLabel.SetSelectable(true);
        nameLabel.SetXalign(0);
        nameLabel.SetMaxWidthChars(40);
        nameLabel.SetEllipsize(Pango.EllipsizeMode.End);

        var addressText = Gtk.Label.New(address);
        addressText.SetEllipsize(Pango.EllipsizeMode.Middle);
        addressText.SetMaxWidthChars(40);
        var addressContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        addressContent.Append(addressText);
        addressContent.Append(WinoIcons.Label(WinoIconGlyph.Copy, 12));
        var addressButton = Gtk.Button.New();
        addressButton.SetChild(addressContent);
        addressButton.AddCssClass("flat");
        addressButton.AddCssClass(WinoCssClass.Recipient);
        addressButton.SetHalign(Gtk.Align.Start);
        addressButton.SetTooltipText(Translator.MacOS_Reader_CopyAddress);
        WinoA11y.Rename(addressButton, $"{Translator.MacOS_Reader_CopyAddress} {address}");

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.Append(nameLabel);
        text.Append(addressButton);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        content.SetMarginTop(12);
        content.SetMarginEnd(14);
        content.SetMarginBottom(12);
        content.SetMarginStart(16);
        content.Append(picture.Root);
        content.Append(text);

        var popover = Gtk.Popover.New();
        popover.SetChild(content);
        popover.SetPosition(Gtk.PositionType.Bottom);
        popover.SetAutohide(true);
        popover.SetParent(anchor);
        _popover = popover;
        _subscriptions.Add(GtkSignals.OnClicked(addressButton, () =>
        {
            CopyRequested?.Invoke(address);
            Close();
        }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) => Teardown(popover)));
        popover.Popup();
    }

    public void Close() => _popover?.Popdown();

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        if (_popover is { } popover) Teardown(popover);
        _disposed = true;
        CopyRequested = null;
    }

    private void Teardown(Gtk.Popover popover)
    {
        if (!ReferenceEquals(_popover, popover)) return;
        _popover = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        // GTK still uses the popover after its closed signal; it leaves its anchor one main-loop turn later.
        MainLoop.Post(popover.Unparent);
    }
}
