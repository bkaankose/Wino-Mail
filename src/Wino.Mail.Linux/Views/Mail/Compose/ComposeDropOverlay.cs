using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Input;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal enum ComposeDropZone
{
    None,
    Files,
    Images
}

/// <summary>
/// The composer's drop zones (Mac <c>ComposeDropOverlay</c>): while files are dragged over the page two cards show, Attach
/// and Insert inline. The target sits on the page in the capture phase, so the drag never reaches the web view. GTK does
/// not show the dragged files before the drop, so the Images card takes any drop and non-images become attachments.
/// </summary>
internal sealed class ComposeDropOverlay : IDisposable
{
    private static readonly string[] InlineImageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly Gtk.Widget _page;
    private readonly FileDropTarget _target;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
    private readonly Gtk.Box _files;
    private readonly Gtk.Box _images;
    private bool _disposed;

    public ComposeDropOverlay(Gtk.Widget page)
    {
        WinoLiveObjects.Track(this);
        _page = page;
        _files = Zone(WinoIconGlyph.Attachment, Translator.ComposerAttachmentsDropZone_Message, Translator.ComposerFilesDropZone_Hint);
        _images = Zone(WinoIconGlyph.Image, Translator.ComposerImagesDropZone_Message, Translator.ComposerImagesDropZone_Hint);
        _root.AddCssClass("wino-compose-drop");
        _root.SetHomogeneous(true);
        _root.Append(_files);
        _root.Append(_images);
        _root.SetCanTarget(false);
        _root.SetVisible(false);

        _target = FileDropTarget.Attach(page, capture: true);
        _target.Entered += (_, x, y) => Track(x, y);
        _target.Moved += (_, x, y) => Track(x, y);
        _target.Left += _ => SetState(false, ComposeDropZone.None);
        _target.Dropped += (_, paths, x, y) => Drop(paths, x, y);
    }

    public Gtk.Widget Root => _root;
    public bool IsActive { get; private set; }
    public ComposeDropZone HoveredZone { get; private set; }
    public FileDropTarget Target => _target;
    public Func<bool>? CanAccept
    {
        get => _target.CanAccept;
        set => _target.CanAccept = value;
    }

    public event Action? StateChanged;
    public event Action<string[]>? FilesDropped;
    public event Action<string[]>? ImagesDropped;

    public static bool IsInlineImage(string path) => InlineImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Shows the cards without a drag, for screenshots.</summary>
    public void SetPreview(bool active, ComposeDropZone hovered) => SetState(active, active ? hovered : ComposeDropZone.None);

    /// <summary>The zone under (<paramref name="x"/>, <paramref name="y"/>) in page coordinates.</summary>
    public ComposeDropZone ZoneAt(double x, double y)
    {
        if (Contains(_images, x, y)) return ComposeDropZone.Images;
        return Contains(_files, x, y) ? ComposeDropZone.Files : ComposeDropZone.None;
    }

    private void Track(double x, double y)
    {
        SetState(true, ZoneAt(x, y));
    }

    private void Drop(string[] paths, double x, double y)
    {
        var zone = ZoneAt(x, y);
        if (zone == ComposeDropZone.None) zone = HoveredZone;
        SetState(false, ComposeDropZone.None);
        if (zone == ComposeDropZone.Images)
        {
            var images = paths.Where(IsInlineImage).ToArray();
            var others = paths.Where(static path => !IsInlineImage(path)).ToArray();
            if (images.Length > 0) ImagesDropped?.Invoke(images);
            if (others.Length > 0) FilesDropped?.Invoke(others);
        }
        else
        {
            FilesDropped?.Invoke(paths);
        }
    }

    private void SetState(bool active, ComposeDropZone hovered)
    {
        if (_disposed || (IsActive == active && HoveredZone == hovered)) return;
        IsActive = active;
        HoveredZone = hovered;
        _root.SetVisible(active);
        SetHovered(_files, hovered == ComposeDropZone.Files);
        SetHovered(_images, hovered == ComposeDropZone.Images);
        StateChanged?.Invoke();
    }

    private static void SetHovered(Gtk.Widget zone, bool hovered)
    {
        if (hovered) zone.AddCssClass("hovered");
        else zone.RemoveCssClass("hovered");
    }

    private bool Contains(Gtk.Widget zone, double x, double y)
        => zone.GetVisible() && zone.ComputeBounds(_page, out var bounds)
            && x >= bounds.GetX() && x < bounds.GetX() + bounds.GetWidth() && y >= bounds.GetY() && y < bounds.GetY() + bounds.GetHeight();

    private static Gtk.Box Zone(WinoIconGlyph glyph, string message, string hint)
    {
        var zone = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        zone.AddCssClass("wino-compose-drop-zone");
        zone.SetValign(Gtk.Align.Fill);
        zone.SetHexpand(true);
        var center = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        center.SetValign(Gtk.Align.Center);
        center.SetVexpand(true);
        center.Append(WinoIcons.Label(glyph, 32));
        var title = Gtk.Label.New(message);
        title.AddCssClass("title-4");
        title.SetWrap(true);
        title.SetJustify(Gtk.Justification.Center);
        var caption = Gtk.Label.New(hint);
        caption.AddCssClass("dim-label");
        caption.SetWrap(true);
        caption.SetJustify(Gtk.Justification.Center);
        center.Append(title);
        center.Append(caption);
        zone.Append(center);
        WinoA11y.Label(zone, message);
        return zone;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target.Dispose();
        StateChanged = null;
        FilesDropped = null;
        ImagesDropped = null;
    }
}
