using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Files dropped on the event composer's side pane become attachments (Windows CalendarEventComposePage
/// AllowDrop). Finder URLs are readable only during the drag, and the ViewModel reads attachment files
/// when the event is saved, so each dropped file is copied into the app's temporary folder first.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private static readonly string DroppedAttachmentsRoot = Path.Combine(Path.GetTempPath(), "WinoEventAttachments");

    /// <summary>Covers <paramref name="pane"/> with a drop target that passes clicks through.</summary>
    private void SetUpAttachmentDrop(NSView pane)
    {
        var overlay = new EventAttachmentDropOverlay(() => ViewModel.CanAddAttachments);
        overlay.FilesDropped += (_, urls) => AttachDroppedFiles(urls);
        WinoLayout.Fill(overlay, pane);
    }

    private void AttachDroppedFiles(IReadOnlyList<NSUrl> urls)
    {
        var folder = Path.Combine(DroppedAttachmentsRoot, Guid.NewGuid().ToString("N"));
        foreach (var url in urls)
        {
            if (url.Path is not { } path || !File.Exists(path)) continue;
            bool scoped = url.StartAccessingSecurityScopedResource();
            try
            {
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileName(path));
                File.Copy(path, copy, overwrite: true);
                ViewModel.TryAddAttachment(copy, new FileInfo(copy).Length);
            }
            catch (IOException exception) { ReportError(exception); }
            catch (UnauthorizedAccessException exception) { ReportError(exception); }
            finally { if (scoped) url.StopAccessingSecurityScopedResource(); }
        }
    }
}

/// <summary>A transparent file drop target that outlines its area with the accent while a file drag is over it.</summary>
internal sealed class EventAttachmentDropOverlay : NSView
{
    private readonly Func<bool> _canDrop;
    private bool _active;

    public EventAttachmentDropOverlay(Func<bool> canDrop)
    {
        _canDrop = canDrop;
        TranslatesAutoresizingMaskIntoConstraints = false;
        RegisterForDraggedTypes(["public.file-url"]);
        AccessibilityElement = false;
    }

    public event EventHandler<IReadOnlyList<NSUrl>>? FilesDropped;

    /// <summary>Clicks, scrolling and typing reach the pane underneath.</summary>
    public override NSView? HitTest(CGPoint point) => null;

    public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
    {
        var allowed = _canDrop() && Urls(sender).Length > 0;
        SetActive(allowed);
        return allowed ? NSDragOperation.Copy : NSDragOperation.None;
    }

    public override NSDragOperation DraggingUpdated(INSDraggingInfo sender) => _active ? NSDragOperation.Copy : NSDragOperation.None;

    public override void DraggingExited(INSDraggingInfo? sender) => SetActive(false);

    public override void DraggingEnded(INSDraggingInfo sender) => SetActive(false);

    public override bool PrepareForDragOperation(INSDraggingInfo sender) => _active;

    public override bool PerformDragOperation(INSDraggingInfo sender)
    {
        var urls = Urls(sender);
        SetActive(false);
        if (urls.Length == 0) return false;
        FilesDropped?.Invoke(this, urls);
        return true;
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (!_active) return;
        var path = NSBezierPath.FromRoundedRect(Bounds.Inset(6, 6), 8, 8);
        WinoStyle.Accent.ColorWithAlphaComponent(0.08f).SetFill();
        path.Fill();
        path.LineWidth = 2;
        path.SetLineDash([6, 4], 0);
        WinoStyle.Accent.SetStroke();
        path.Stroke();
    }

    private static NSUrl[] Urls(INSDraggingInfo info)
    {
        var options = new NSDictionary(new NSString("NSPasteboardURLReadingFileURLsOnlyKey"), NSNumber.FromBoolean(true));
        return info.DraggingPasteboard.ReadObjectsForClasses([new Class(typeof(NSUrl))], options)?.OfType<NSUrl>()
            .Where(url => url.Path is { } path && !Directory.Exists(path)).ToArray() ?? [];
    }
}
