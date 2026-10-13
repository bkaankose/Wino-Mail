using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>AppKit images for mail operations; titles and glyphs come from the shared <see cref="MailOperationDisplay"/>.</summary>
internal static class MailOperationPresentation
{
    /// <summary>A menu or button image for the operation; null when it has no glyph.</summary>
    public static AppKit.NSImage? Image(MailOperation operation, double size = 16, AppKit.NSColor? tint = null)
        => MailOperationDisplay.GetGlyph(operation) is var glyph && glyph != WinoIconGlyph.None
            ? Presentation.AppKit.WinoIcons.Image(glyph, size, tint, MailOperationDisplay.GetTitle(operation))
            : null;
}
