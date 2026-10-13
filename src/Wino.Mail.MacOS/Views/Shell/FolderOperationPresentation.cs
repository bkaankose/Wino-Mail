using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// AppKit menu titles and images for folder context actions; titles, glyphs and the destructive
/// flag come from the shared <see cref="FolderOperationDisplay"/>.
/// </summary>
internal static class FolderOperationPresentation
{
    /// <summary>The menu title; actions that open a dialog for more input end with an ellipsis (macOS convention).</summary>
    public static string MenuTitle(FolderOperation operation)
    {
        var title = FolderOperationDisplay.GetTitle(operation);
        return FolderOperationDisplay.AsksForInput(operation) && title.Length > 0 ? $"{title}…" : title;
    }

    /// <summary>A menu image for the operation; null when it has no glyph.</summary>
    public static NSImage? Image(FolderOperation operation, double size = 16)
        => FolderOperationDisplay.GetGlyph(operation) is var glyph && glyph != WinoIconGlyph.None
            ? WinoIcons.Image(glyph, size, null, FolderOperationDisplay.GetTitle(operation))
            : null;
}
