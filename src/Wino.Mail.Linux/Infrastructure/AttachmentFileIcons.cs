namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// File-type icons from the desktop's MIME database, by file name: the one place where theme icons stand for content,
/// as <c>NSWorkspace</c> icons do on macOS. Main thread only.
/// </summary>
internal static class AttachmentFileIcons
{
    private static readonly Dictionary<string, Gio.Icon> Cache = new(StringComparer.Ordinal);

    public static Gio.Icon For(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (Cache.TryGetValue(extension, out var cached)) return cached;
        var type = Gio.Functions.ContentTypeGuess("file" + extension, null, out _);
        var icon = Gio.Functions.ContentTypeGetIcon(type);
        Cache[extension] = icon;
        return icon;
    }

    public static Gtk.Image Image(string? fileName, int pixelSize)
    {
        var image = Gtk.Image.NewFromGicon(For(fileName));
        image.AccessibleRole = Gtk.AccessibleRole.Presentation;
        image.SetPixelSize(pixelSize);
        return image;
    }
}
