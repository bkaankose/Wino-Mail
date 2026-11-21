using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's privacy popover (Windows <c>PrivacyFlyout</c>, macOS privacy menu): what was loaded or blocked, the
/// blocked hosts, then the actions of <c>PrivacyMenuItems</c> with their hints. Built from the ViewModel when shown.
/// </summary>
// A Gio.Menu cannot show two-line items, disabled header lines or a host list, so this is a popover of its own.
internal sealed class ReaderPrivacyPopover : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private Gtk.Popover? _popover;
    private bool _disposed;

    public ReaderPrivacyPopover() => WinoLiveObjects.Track(this);

    public event Action<PrivacyMenuItem>? ItemInvoked;

    public bool IsShown => _popover?.GetVisible() == true;

    public string Describe() => _popover is { } popover && popover.GetVisible() ? "shown" : "hidden";

    public void Show(Gtk.Widget anchor, MailRenderingPageViewModel viewModel)
    {
        if (_disposed || !viewModel.HasPrivacyDecision || anchor.GetRoot() is null) return;
        if (_popover is { } previous)
        {
            previous.Popdown();
            Teardown(previous);
        }

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        content.SetSizeRequest(320, -1);
        content.SetMarginTop(6);
        content.SetMarginBottom(6);
        content.SetMarginStart(6);
        content.SetMarginEnd(6);

        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        header.SetMarginStart(6);
        header.SetMarginEnd(6);
        header.SetMarginTop(4);
        var headerIcon = WinoIcons.Label(viewModel.PrivacyButtonIcon, 16);
        headerIcon.SetValign(Gtk.Align.Start);
        header.Append(headerIcon);
        var headerText = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        headerText.SetHexpand(true);
        headerText.Append(Text(viewModel.PrivacyPolicyLine, "heading"));
        if (!string.IsNullOrWhiteSpace(viewModel.PrivacyReasonLine)) headerText.Append(Text(viewModel.PrivacyReasonLine, "caption", "dim-label"));
        header.Append(headerText);
        content.Append(header);

        if (viewModel.HasPrivacyDetailLine)
        {
            var detail = Text(viewModel.PrivacyDetailLine, "dim-label");
            detail.SetMarginStart(6);
            detail.SetMarginEnd(6);
            content.Append(detail);
        }

        foreach (var host in viewModel.PrivacyBlockedHosts.ToArray())
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.SetMarginStart(6);
            row.SetMarginEnd(6);
            var glyph = WinoIcons.Label(WinoIconGlyph.Dismiss, 11);
            glyph.AddCssClass("error");
            row.Append(glyph);
            var name = Text(host.Host, "monospace", "caption");
            name.SetWrap(false);
            name.SetEllipsize(Pango.EllipsizeMode.Middle);
            name.SetMaxWidthChars(28);
            row.Append(name);
            var description = Text(host.Description, "caption", "dim-label");
            description.SetHexpand(true);
            row.Append(description);
            content.Append(row);
        }

        content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        var first = true;
        foreach (var item in viewModel.PrivacyMenuItems.ToArray())
        {
            if (item.StartsGroup && !first) content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
            first = false;
            content.Append(ItemButton(item));
        }

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetMaxContentHeight(480);
        scroller.SetChild(content);

        var popover = Gtk.Popover.New();
        popover.SetChild(scroller);
        popover.SetPosition(Gtk.PositionType.Bottom);
        popover.SetAutohide(true);
        popover.SetParent(anchor);
        _popover = popover;
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
        ItemInvoked = null;
    }

    private Gtk.Button ItemButton(PrivacyMenuItem item)
    {
        var text = Text(item.Text);
        if (item.IsRecommended) text.AddCssClass("heading");
        var lines = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        lines.SetHexpand(true);
        lines.Append(text);
        if (item.HasHint) lines.Append(Text(item.Hint ?? string.Empty, "caption", "dim-label"));
        var icon = WinoIcons.Label(item.Icon, 16);
        icon.SetValign(Gtk.Align.Start);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        row.Append(icon);
        row.Append(lines);

        var button = Gtk.Button.New();
        button.SetChild(row);
        button.AddCssClass("flat");
        button.AddCssClass("wino-privacy-item");
        button.SetHalign(Gtk.Align.Fill);
        WinoA11y.Label(button, item.Text);
        WinoA11y.Description(button, item.Hint);
        _subscriptions.Add(GtkSignals.OnClicked(button, () =>
        {
            Close();
            ItemInvoked?.Invoke(item);
        }));
        return button;
    }

    private static Gtk.Label Text(string text, params string[] classes)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.SetMaxWidthChars(44);
        foreach (var name in classes) label.AddCssClass(name);
        return label;
    }

    private void Teardown(Gtk.Popover popover)
    {
        if (!ReferenceEquals(_popover, popover)) return;
        _popover = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        MainLoop.Post(popover.Unparent);
    }
}
