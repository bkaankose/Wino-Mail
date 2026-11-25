namespace Wino.Editor.WebKitGtk;

// WebKitGTK never hands clipboard images to the page's paste event, so an image is read here and inserted by script.
// Gir.Core 0.8.1 does not wrap gdk_clipboard_read_texture_async; its public but unversioned Internal layer stays in this file.
internal static class WebKitGtkClipboard
{
    public static bool OffersImageOnly(Gdk.Clipboard clipboard)
    {
        var formats = clipboard.GetFormats();
        return formats.ContainGtype(Gdk.Texture.GetGType())
            && !formats.ContainMimeType("text/plain")
            && !formats.ContainMimeType("text/plain;charset=utf-8")
            && !formats.ContainMimeType("text/html");
    }

    public static Task<Gdk.Texture> ReadTextureAsync(Gdk.Clipboard clipboard)
    {
        var done = new TaskCompletionSource<Gdk.Texture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Gio.Internal.AsyncReadyCallbackAsyncHandler((_, result, _) =>
        {
            try { done.TrySetResult(clipboard.ReadTextureFinish(result) ?? throw new InvalidOperationException("The clipboard offered no image.")); }
            catch (Exception error) { done.TrySetException(error); }
        });
        Gdk.Internal.Clipboard.ReadTextureAsync(clipboard.Handle.DangerousGetHandle(), IntPtr.Zero, handler.NativeCallback, IntPtr.Zero);
        return done.Task;
    }

    public static string ToPngDataUri(Gdk.Texture texture)
    {
        using var bytes = texture.SaveToPngBytes();
        return "data:image/png;base64," + Convert.ToBase64String(bytes.GetRegionSpan<byte>(0, bytes.GetSize()));
    }
}
