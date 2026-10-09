using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Common;
using Wino.Editor;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Files dropped on the composer (Windows ComposePage file and image drop zones). A transparent
/// overlay above the composer registers only for file URLs, so text and HTML drags inside the
/// WKWebView editor keep working, while Finder files land here: the overlay shows a files zone
/// (any file becomes an attachment) and an images zone (JPG and PNG are inserted inline at the caret).
/// Dropped file URLs are readable only during the drag, so their bytes are read immediately.
/// </summary>
public sealed partial class ComposePageViewController
{
    private static readonly string[] InlineImageExtensions = [".jpg", ".jpeg", ".png"];
    private ComposeDropOverlay? _dropOverlay;

    /// <summary>Adds the drop overlay above the composer. Called once at the end of LoadView.</summary>
    private void SetUpFileDrop()
    {
        _dropOverlay = new ComposeDropOverlay();
        _dropOverlay.StateChanged += (_, _) => SyncDropState();
        _dropOverlay.FilesDropped += (_, files) => AttachDroppedFiles(files);
        _dropOverlay.ImagesDropped += (_, files) => Observe(InsertDroppedImagesAsync(files));
        WinoLayout.Fill(_dropOverlay, View);
#if DEBUG
        RegisterDropDebugCommands();
#endif
    }

    /// <summary>Windows IsDraggingOverComposerGrid / IsDraggingOverFilesDropZone / IsDraggingOverImagesDropZone.</summary>
    private void SyncDropState()
    {
        if (_dropOverlay is null) return;
        ViewModel.IsDraggingOverComposerGrid = _dropOverlay.IsActive;
        ViewModel.IsDraggingOverFilesDropZone = _dropOverlay.HoveredZone == ComposeDropZone.Files;
        ViewModel.IsDraggingOverImagesDropZone = _dropOverlay.HoveredZone == ComposeDropZone.Images;
    }

    private void AttachDroppedFiles(IReadOnlyList<SharedFile> files)
    {
        foreach (var file in files) ViewModel.AddAttachment(file);
    }

    private async Task InsertDroppedImagesAsync(IReadOnlyList<SharedFile> files)
    {
        if (_editor is null || _editorDisposed) return;
        var images = files
            .Where(static file => IsInlineImage(file.FullFilePath))
            .Select(static file => new EditorImageInfo($"data:{ImageMimeType(file.FullFilePath)};base64,{Convert.ToBase64String(file.Data)}", Path.GetFileName(file.FullFilePath)))
            .ToList();
        if (images.Count > 0) await _editor.InsertImagesAsync(images);
    }

    internal static bool IsInlineImage(string path) => InlineImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string ImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";

#if DEBUG
    /// <summary>
    /// Debug bridge: "compose-drop-overlay [on|files|images|off]" shows the overlay (optionally with a
    /// hovered zone) for a screenshot; "compose-drop files|images PATH…" runs the drop path for local files.
    /// </summary>
    private void RegisterDropDebugCommands()
    {
        Infrastructure.MacDebugBridge.Register("compose-drop-overlay", args =>
        {
            if (_dropOverlay is null) return Task.FromResult("no overlay");
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "on";
            _dropOverlay.Preview(mode != "off", mode switch { "files" => ComposeDropZone.Files, "images" => ComposeDropZone.Images, _ => ComposeDropZone.None });
            return Task.FromResult($"overlay={_dropOverlay.IsActive} zone={_dropOverlay.HoveredZone}");
        });
        Infrastructure.MacDebugBridge.Register("compose-drop", async args =>
        {
            if (args.Length < 2) return "usage: compose-drop files|images PATH…";
            var files = ComposeDropOverlay.ReadFiles(args.Skip(1).Select(path => NSUrl.FromFilename(path)));
            if (args[0].StartsWith("i", StringComparison.OrdinalIgnoreCase))
            {
                int images = files.Count(file => IsInlineImage(file.FullFilePath));
                if (images == 0) return "refused: no JPG or PNG files";
                await InsertDroppedImagesAsync(files);
                return $"inserted {images} image(s)";
            }
            AttachDroppedFiles(files);
            return $"attached {files.Count}; attachments={ViewModel.IncludedAttachments.Count}";
        });
    }
#endif
}

internal enum ComposeDropZone
{
    None,
    Files,
    Images
}

/// <summary>
/// The composer's drop overlay. It never takes mouse clicks (hit testing passes through) and draws
/// nothing until a file drag enters; then it dims the composer and shows the two zones. The hovered
/// zone takes an accent dashed outline and an accent tint; the images zone refuses drags without a
/// JPG or PNG file.
/// </summary>
internal sealed class ComposeDropOverlay : NSView
{
    private readonly DropZoneView _files;
    private readonly DropZoneView _images;
    private bool _active;
    private bool _dragHasImages;
    private ComposeDropZone _hovered;

    public ComposeDropOverlay()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _files = new DropZoneView(WinoIconGlyph.Attachment, Translator.ComposerAttachmentsDropZone_Message, Translator.ComposerFilesDropZone_Hint);
        _images = new DropZoneView(WinoIconGlyph.Image, Translator.ComposerImagesDropZone_Message, Translator.ComposerImagesDropZone_Hint);
        var zones = WinoLayout.HStack(16, _files, _images);
        zones.Distribution = NSStackViewDistribution.FillEqually;
        zones.Alignment = NSLayoutAttribute.Height;
        zones.Hidden = true;
        WinoLayout.Fill(zones, this, 24, 24, 24, 24);
        Zones = zones;
        RegisterForDraggedTypes([NSPasteboard.NSPasteboardTypeFileUrl]);
        AccessibilityElement = false;
    }

    private NSView Zones { get; }

    public bool IsActive => _active;
    public ComposeDropZone HoveredZone => _hovered;

    public event EventHandler? StateChanged;
    public event EventHandler<IReadOnlyList<SharedFile>>? FilesDropped;
    public event EventHandler<IReadOnlyList<SharedFile>>? ImagesDropped;

    public override bool IsFlipped => true;

    /// <summary>Clicks, typing and text drags reach the composer underneath.</summary>
    public override NSView? HitTest(CGPoint point) => null;

    public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
    {
        _dragHasImages = Urls(sender).Any(url => url.Path is { } path && ComposePageViewController.IsInlineImage(path));
        SetActive(true);
        return Track(sender);
    }

    public override NSDragOperation DraggingUpdated(INSDraggingInfo sender) => Track(sender);

    public override void DraggingExited(INSDraggingInfo? sender) => SetActive(false);

    public override bool PrepareForDragOperation(INSDraggingInfo sender) => _hovered != ComposeDropZone.None;

    public override bool PerformDragOperation(INSDraggingInfo sender)
    {
        var zone = _hovered;
        var files = ReadFiles(Urls(sender));
        SetActive(false);
        if (files.Count == 0) return false;
        if (zone == ComposeDropZone.Images)
        {
            if (!files.Any(file => ComposePageViewController.IsInlineImage(file.FullFilePath))) return false;
            ImagesDropped?.Invoke(this, files);
        }
        else if (zone == ComposeDropZone.Files)
        {
            FilesDropped?.Invoke(this, files);
        }
        else return false;
        return true;
    }

    public override void DraggingEnded(INSDraggingInfo sender) => SetActive(false);

    /// <summary>Shows the overlay without a drag (debug screenshots).</summary>
    public void Preview(bool active, ComposeDropZone zone)
    {
        _dragHasImages = true;
        SetActive(active);
        if (active) SetHovered(zone);
    }

    private NSDragOperation Track(INSDraggingInfo info)
    {
        var point = ConvertPointFromView(info.DraggingLocation, null);
        var zone = _images.Frame.Contains(_images.Superview!.ConvertPointFromView(point, this)) ? ComposeDropZone.Images
            : _files.Frame.Contains(_files.Superview!.ConvertPointFromView(point, this)) ? ComposeDropZone.Files
            : ComposeDropZone.None;
        if (zone == ComposeDropZone.Images && !_dragHasImages) zone = ComposeDropZone.None;
        SetHovered(zone);
        return zone == ComposeDropZone.None ? NSDragOperation.None : NSDragOperation.Copy;
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Zones.Hidden = !active;
        _images.Disabled = !_dragHasImages;
        if (!active) _hovered = ComposeDropZone.None;
        _files.Hovered = _hovered == ComposeDropZone.Files;
        _images.Hovered = _hovered == ComposeDropZone.Images;
        NeedsDisplay = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHovered(ComposeDropZone zone)
    {
        if (_hovered == zone) return;
        _hovered = zone;
        _files.Hovered = zone == ComposeDropZone.Files;
        _images.Hovered = zone == ComposeDropZone.Images;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (!_active) return;
        WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.86), WinoStyle.Hex(0x1E1E1E, 0.88)).SetFill();
        NSBezierPath.FromRect(Bounds).Fill();
    }

    private static NSUrl[] Urls(INSDraggingInfo info)
    {
        var options = new NSDictionary(new NSString("NSPasteboardURLReadingFileURLsOnlyKey"), NSNumber.FromBoolean(true));
        return info.DraggingPasteboard.ReadObjectsForClasses([new Class(typeof(NSUrl))], options)?.OfType<NSUrl>().ToArray() ?? [];
    }

    /// <summary>Reads the dropped files now: sandbox access to Finder URLs lasts only for the drag.</summary>
    public static List<SharedFile> ReadFiles(IEnumerable<NSUrl> urls)
    {
        var files = new List<SharedFile>();
        foreach (var url in urls)
        {
            if (url.Path is not { } path || Directory.Exists(path) || !File.Exists(path)) continue;
            bool scoped = url.StartAccessingSecurityScopedResource();
            try { files.Add(new SharedFile(path, File.ReadAllBytes(path))); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { if (scoped) url.StopAccessingSecurityScopedResource(); }
        }
        return files;
    }

    /// <summary>One drop zone: a dashed rounded outline, a glyph, the zone message and its hint.</summary>
    private sealed class DropZoneView : NSView
    {
        private readonly WinoIconView _glyph;
        private bool _hovered;
        private bool _disabled;

        public DropZoneView(WinoIconGlyph glyph, string title, string hint)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _glyph = new WinoIconView(glyph, 28, WinoStyle.SecondaryText);
            var titleLabel = WinoStyle.Label(title, WinoStyle.Heading, WinoStyle.PrimaryText);
            titleLabel.Alignment = NSTextAlignment.Center;
            var hintLabel = WinoStyle.Label(hint, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
            hintLabel.Alignment = NSTextAlignment.Center;
            var stack = WinoLayout.VStack(8, _glyph, titleLabel, hintLabel);
            stack.Alignment = NSLayoutAttribute.CenterX;
            AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints(
            [
                stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
                stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                stack.WidthAnchor.ConstraintLessThanOrEqualTo(WidthAnchor, 1, -24)
            ]);
        }

        public bool Hovered
        {
            get => _hovered;
            set { _hovered = value; _glyph.Tint = value ? WinoStyle.Accent : WinoStyle.SecondaryText; NeedsDisplay = true; }
        }

        public bool Disabled
        {
            get => _disabled;
            set { _disabled = value; AlphaValue = value ? 0.45f : 1; }
        }

        public override bool IsFlipped => true;

        public override void DrawRect(CGRect dirtyRect)
        {
            var rect = Bounds.Inset(1, 1);
            var path = NSBezierPath.FromRoundedRect(rect, 8, 8);
            if (_hovered)
            {
                WinoStyle.Accent.ColorWithAlphaComponent(0.08f).SetFill();
                path.Fill();
            }
            path.LineWidth = _hovered ? 2 : 1.5f;
            path.SetLineDash([6, 4], 0);
            (_hovered ? WinoStyle.Accent : WinoStyle.TertiaryText).SetStroke();
            path.Stroke();
        }
    }
}
