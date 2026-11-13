using AppKit;
using UniformTypeIdentifiers;

namespace Wino.Mail.MacOS.Infrastructure;

internal static class AttachmentFileIcons
{
    private static readonly Dictionary<string, NSImage> IconsByExtension = new(StringComparer.OrdinalIgnoreCase);

    public static NSImage Icon(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        if (IconsByExtension.TryGetValue(extension, out var cached)) return cached;
        var type = string.IsNullOrEmpty(extension) ? null : UTType.CreateFromExtension(extension);
        var icon = NSWorkspace.SharedWorkspace.GetIcon(type ?? UTTypes.Data);
        IconsByExtension[extension] = icon;
        return icon;
    }

    public static NSImageView ImageView(string? fileName, nfloat size)
    {
        var view = new NSImageView { Image = Icon(fileName), ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false };
        view.WidthAnchor.ConstraintEqualTo(size).Active = true;
        view.HeightAnchor.ConstraintEqualTo(size).Active = true;
        return view;
    }
}
