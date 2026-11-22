using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    internal ComposeAttachmentTray? AttachmentTray { get; private set; }
    internal ComposeDropOverlay? DropOverlay { get; private set; }

    partial void InitializeAttachments()
    {
        var tray = AttachmentTray = new ComposeAttachmentTray(ViewModel, Dispatcher, ReportError);
        AttachmentSlot.Append(tray.Root);

        if (FormatToolbar is { } toolbar)
        {
            var attach = new EditorFormatButton(WinoIconGlyph.Attachment, Translator.Composer_AttachFiles, AttachFiles, tooltip: Translator.Composer_AttachFilesDescription);
            Bindings.Own(attach);
            toolbar.PrependInsertItems(EditorFormatToolbar.Item(attach, menu => menu.Item(Translator.Composer_AttachFiles, AttachFiles, icon: WinoIconGlyph.Attachment)));
        }

        var overlay = DropOverlay = new ComposeDropOverlay(PageOverlay);
        PageOverlay.AddOverlay(overlay.Root);
        overlay.StateChanged += SyncDropState;
        overlay.FilesDropped += paths => Observe(AttachPathsAsync(paths));
        overlay.ImagesDropped += paths => Observe(InsertImagePathsAsync(paths));

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => HandlePasteKey(args.Keyval, args.State), false));
        EditorHost.AddController(keys);
        Bindings.Own(new ActionDisposable(() => EditorHost.RemoveController(keys)));
    }

    partial void DeactivateAttachments()
    {
        if (DropOverlay is { } overlay)
        {
            overlay.Dispose();
            PageOverlay.RemoveOverlay(overlay.Root);
            DropOverlay = null;
        }
        if (AttachmentTray is { } tray)
        {
            tray.Dispose();
            AttachmentSlot.Remove(tray.Root);
            AttachmentTray = null;
        }
    }

    private void AttachFiles()
    {
        if (ViewModel.AttachFilesCommand.CanExecute(null)) Observe(ViewModel.AttachFilesCommand.ExecuteAsync(null));
    }

    private void SyncDropState()
    {
        if (DropOverlay is not { } overlay || IsReleased) return;
        ViewModel.IsDraggingOverComposerGrid = overlay.IsActive;
        ViewModel.IsDraggingOverFilesDropZone = overlay.HoveredZone == ComposeDropZone.Files;
        ViewModel.IsDraggingOverImagesDropZone = overlay.HoveredZone == ComposeDropZone.Images;
    }

    // A path from the document portal (/run/user/<uid>/doc/…) is readable only while the drag's grant lasts: read it now.
    internal async Task AttachPathsAsync(string[] paths)
    {
        foreach (var path in paths)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(exception, "A dropped file could not be read and was skipped.");
                continue;
            }
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!IsReleased) ViewModel.AddAttachment(new SharedFile(path, data));
            });
        }
    }

    internal async Task InsertImagePathsAsync(string[] paths)
    {
        var images = new List<Wino.Editor.EditorImageInfo>();
        foreach (var path in paths)
        {
            if (await EditorDialogs.ImageFromFileAsync(path) is { } image) images.Add(image);
        }
        if (images.Count == 0 || IsReleased || Editor is not { } editor || _editorDisposed) return;
        await editor.InsertImagesAsync(images);
    }

    /// <summary>Ctrl+V with only an image on the clipboard: WebKitGTK cannot paste it, so the editor session reads it.</summary>
    private bool HandlePasteKey(uint keyval, Gdk.ModifierType state)
    {
        var modifiers = state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask);
        if (modifiers != Gdk.ModifierType.ControlMask || (keyval != Gdk.Constants.KEY_v && keyval != Gdk.Constants.KEY_V)) return false;
        return PasteImageIfOnly();
    }

    internal bool PasteImageIfOnly()
    {
        if (Editor is not { } editor || _editorDisposed || !editor.ClipboardOffersImageOnly) return false;
        Observe(editor.PasteClipboardImageAsync());
        return true;
    }

#if DEBUG
    internal (double X, double Y) DebugZoneCentre(ComposeDropZone zone)
    {
        if (DropOverlay is not { } overlay) return (0, 0);
        for (var y = 0; y < PageOverlay.GetHeight(); y += 8)
            for (var x = 0; x < PageOverlay.GetWidth(); x += 8)
                if (overlay.ZoneAt(x, y) == zone) return (x + 40, y + 40);
        return (PageOverlay.GetWidth() / 2.0, PageOverlay.GetHeight() / 2.0);
    }
#endif
}
