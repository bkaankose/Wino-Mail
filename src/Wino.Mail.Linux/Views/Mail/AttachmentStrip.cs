using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's attachment strip (macOS layout): one row of tiles (file-type icon, name, type and size) that scrolls
/// sideways, and "Save all attachments" for two or more. A click opens a tile; its context menu offers Open and Save.
/// </summary>
internal sealed class AttachmentStrip : IDisposable
{
    public const int TileHeight = 48;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly Gtk.ScrolledWindow _scroller = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _tiles = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.Button _saveAll = Gtk.Button.New();
    private readonly Gtk.Label _saveAllText = Gtk.Label.New(Translator.Reader_SaveAllAttachmentButtonText);
    private readonly IDisposable _saveAllClicked;
    private readonly List<Tile> _current = [];
    private MailAttachmentViewModel[] _attachments = [];
    private bool _disposed;

    private sealed class Tile
    {
        public required MailAttachmentViewModel Attachment { get; init; }
        public required Gtk.Button Button { get; init; }
        public required Adw.Bin IconSlot { get; init; }
        public required Gtk.Label Details { get; init; }
        public List<IDisposable> Subscriptions { get; } = [];
        public PropertyChangedEventHandler? Changed { get; set; }
        public bool? Busy { get; set; }
    }

    public AttachmentStrip()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass("wino-attachment-strip");
        _root.SetVisible(false);
        // libadwaita draws the undershoot shadows of a scrolled window whose content continues: the GNOME form of the macOS fade.
        _scroller.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        _scroller.SetPropagateNaturalHeight(true);
        _scroller.SetOverlayScrolling(true);
        _scroller.SetHexpand(true);
        _scroller.SetChild(_tiles);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        content.Append(WinoIcons.Label(WinoIconGlyph.ArrowDownload, 12));
        content.Append(_saveAllText);
        _saveAll.SetChild(content);
        _saveAll.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(_saveAll, Translator.Reader_SaveAllAttachmentButtonText);
        _saveAll.SetTooltipText(Translator.Reader_SaveAllAttachmentButtonText);
        _saveAllClicked = GtkSignals.OnClicked(_saveAll, () => SaveAllRequested?.Invoke());
        _root.Append(_scroller);
        _root.Append(_saveAll);
    }

    public Gtk.Widget Root => _root;

    public event Action<MailAttachmentViewModel>? OpenRequested;
    public event Action<MailAttachmentViewModel>? SaveRequested;
    public event Action? SaveAllRequested;

    public void SetAttachments(MailAttachmentViewModel[] attachments)
    {
        if (_disposed) return;
        if (Same(attachments)) return;
        Clear();
        _attachments = attachments;
        foreach (var attachment in attachments) _current.Add(CreateTile(attachment));
        _root.SetVisible(attachments.Length > 0);
        _saveAll.SetVisible(attachments.Length >= 2);
        _scroller.GetHadjustment().SetValue(0);
    }

    /// <summary>The compact reader shows the Save all glyph without its text; the tooltip and the name stay.</summary>
    public void SetCompact(bool compact) => _saveAllText.SetVisible(!compact);

    public string Describe()
        => $"attachments={_attachments.Length} saveAll={_saveAll.GetVisible()} names=[{string.Join(", ", _attachments.Select(static attachment => attachment.FileName))}]";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveAllClicked.Dispose();
        Clear();
        OpenRequested = null;
        SaveRequested = null;
        SaveAllRequested = null;
    }

    internal MailAttachmentViewModel? DebugAttachment(int index) => index >= 0 && index < _attachments.Length ? _attachments[index] : null;

    private bool Same(MailAttachmentViewModel[] attachments)
    {
        if (attachments.Length != _attachments.Length) return false;
        for (var index = 0; index < attachments.Length; index++)
            if (!ReferenceEquals(attachments[index], _attachments[index])) return false;
        return true;
    }

    private void Clear()
    {
        foreach (var tile in _current)
        {
            foreach (var subscription in tile.Subscriptions) subscription.Dispose();
            if (tile.Changed is { } changed) tile.Attachment.PropertyChanged -= changed;
            _tiles.Remove(tile.Button);
        }
        _current.Clear();
        _attachments = [];
    }

    private Tile CreateTile(MailAttachmentViewModel attachment)
    {
        var iconSlot = Adw.Bin.New();
        iconSlot.SetValign(Gtk.Align.Center);
        var name = Gtk.Label.New(attachment.FileName);
        name.AddCssClass("wino-attachment-name");
        name.SetEllipsize(Pango.EllipsizeMode.Middle);
        // The viewport gives its child the minimum width, so the name asks for its own length up to the cap.
        name.SetWidthChars(Math.Min((attachment.FileName ?? string.Empty).Length, 24));
        name.SetMaxWidthChars(24);
        name.SetXalign(0);
        var details = Gtk.Label.New(attachment.DetailsText);
        details.AddCssClass("caption");
        details.AddCssClass("dim-label");
        details.SetXalign(0);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetValign(Gtk.Align.Center);
        text.Append(name);
        text.Append(details);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        row.Append(iconSlot);
        row.Append(text);
        var button = Gtk.Button.New();
        button.SetChild(row);
        button.AddCssClass(WinoCssClass.Attachment);
        button.SetSizeRequest(-1, TileHeight);
        button.SetTooltipText(attachment.FileName);
        var tile = new Tile { Attachment = attachment, Button = button, IconSlot = iconSlot, Details = details };
        Refresh(tile);
        tile.Subscriptions.Add(GtkSignals.OnClicked(button, () => OpenRequested?.Invoke(attachment)));
        tile.Subscriptions.Add(ContextMenu.Attach(button, builder => builder
            .Item(Translator.Buttons_Open, () => OpenRequested?.Invoke(attachment), icon: WinoIconGlyph.Open)
            .Item(Translator.Buttons_Save, () => SaveRequested?.Invoke(attachment), icon: WinoIconGlyph.Save)));
        tile.Changed = (_, args) =>
        {
            if (args.PropertyName is not (nameof(MailAttachmentViewModel.IsBusy) or nameof(MailAttachmentViewModel.DetailsText) or null)) return;
            GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
            {
                if (!_disposed && _current.Contains(tile)) Refresh(tile);
                return false;
            });
        };
        attachment.PropertyChanged += tile.Changed;
        _tiles.Append(button);
        return tile;
    }

    private static void Refresh(Tile tile)
    {
        var attachment = tile.Attachment;
        var busy = attachment.IsBusy;
        if (tile.Busy != busy)
        {
            tile.Busy = busy;
            if (busy)
            {
                var spinner = Adw.Spinner.New();
                spinner.SetSizeRequest(32, 32);
                tile.IconSlot.SetChild(spinner);
            }
            else tile.IconSlot.SetChild(AttachmentFileIcons.Image(attachment.FileName, 32));
        }
        tile.Details.SetText(attachment.DetailsText);
        WinoA11y.Rename(tile.Button, $"{attachment.FileName}, {attachment.DetailsText}");
    }
}
