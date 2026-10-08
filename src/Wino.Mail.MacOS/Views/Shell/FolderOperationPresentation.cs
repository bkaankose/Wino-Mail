using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Titles and Wino glyphs for folder context actions, mirroring the Windows FolderOperationFlyout
/// (XamlHelpers.GetOperationString and GetPathGeometry).
/// </summary>
internal static class FolderOperationPresentation
{
    public static string Title(FolderOperation operation) => operation switch
    {
        FolderOperation.Pin => Translator.FolderOperation_Pin,
        FolderOperation.Unpin => Translator.FolderOperation_Unpin,
        FolderOperation.MarkAllAsRead => Translator.FolderOperation_MarkAllAsRead,
        FolderOperation.DontSync => Translator.FolderOperation_DontSync,
        FolderOperation.Empty => Translator.FolderOperation_Empty,
        FolderOperation.Rename => Translator.FolderOperation_Rename,
        FolderOperation.Delete => Translator.FolderOperation_Delete,
        FolderOperation.Move => Translator.FolderOperation_Move,
        FolderOperation.CreateSubFolder => Translator.FolderOperation_CreateSubFolder,
        _ => string.Empty
    };

    /// <summary>The menu title; actions that open a dialog for more input end with an ellipsis (macOS convention).</summary>
    public static string MenuTitle(FolderOperation operation)
    {
        var title = Title(operation);
        return operation is FolderOperation.Rename or FolderOperation.Move or FolderOperation.CreateSubFolder && title.Length > 0 ? $"{title}…" : title;
    }

    public static WinoIconGlyph Glyph(FolderOperation operation) => operation switch
    {
        FolderOperation.Pin => WinoIconGlyph.Pin,
        FolderOperation.Unpin => WinoIconGlyph.UnPin,
        FolderOperation.MarkAllAsRead => WinoIconGlyph.MarkRead,
        FolderOperation.DontSync => WinoIconGlyph.DontSync,
        FolderOperation.Empty => WinoIconGlyph.EmptyFolder,
        FolderOperation.Rename => WinoIconGlyph.Rename,
        FolderOperation.Delete => WinoIconGlyph.Delete,
        FolderOperation.Move => WinoIconGlyph.Forward,
        FolderOperation.TurnOffNotifications => WinoIconGlyph.TurnOfNotifications,
        FolderOperation.CreateSubFolder => WinoIconGlyph.CreateFolder,
        _ => WinoIconGlyph.None
    };

    /// <summary>Delete and Empty remove mail, so Windows marks them destructive.</summary>
    public static bool IsDestructive(FolderOperation operation) => operation is FolderOperation.Delete or FolderOperation.Empty;

    /// <summary>A menu image for the operation; null when it has no glyph.</summary>
    public static NSImage? Image(FolderOperation operation, double size = 16)
        => Glyph(operation) is var glyph && glyph != WinoIconGlyph.None ? WinoIcons.Image(glyph, size, null, Title(operation)) : null;
}
