using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Titles and Wino glyphs for mail operations, mirroring XamlHelpers.GetOperationString on Windows.</summary>
internal static class MailOperationPresentation
{
    public static string Title(MailOperation operation) => operation switch
    {
        MailOperation.Archive => Translator.MailOperation_Archive,
        MailOperation.UnArchive => Translator.MailOperation_Unarchive,
        MailOperation.SoftDelete or MailOperation.HardDelete => Translator.MailOperation_Delete,
        MailOperation.Move => Translator.MailOperation_Move,
        MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
        MailOperation.MoveToFocused => Translator.MailOperation_MoveFocused,
        MailOperation.MoveToOther => Translator.MailOperation_MoveOther,
        MailOperation.AlwaysMoveToOther => Translator.MailOperation_AlwaysMoveOther,
        MailOperation.AlwaysMoveToFocused => Translator.MailOperation_AlwaysMoveFocused,
        MailOperation.SetFlag => Translator.MailOperation_SetFlag,
        MailOperation.ClearFlag => Translator.MailOperation_ClearFlag,
        MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
        MailOperation.MarkAsUnread => Translator.MailOperation_MarkAsUnread,
        MailOperation.MarkAsNotJunk => Translator.MailOperation_MarkNotJunk,
        MailOperation.Ignore => Translator.MailOperation_Ignore,
        MailOperation.Reply => Translator.MailOperation_Reply,
        MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
        MailOperation.Forward => Translator.MailOperation_Forward,
        MailOperation.Zoom => Translator.MailOperation_Zoom,
        MailOperation.SaveAs => Translator.MailOperation_SaveAs,
        MailOperation.SaveAsPdf => Translator.Buttons_PDF,
        MailOperation.SaveAsEml => Translator.Buttons_EML,
        MailOperation.Find => Translator.MailOperation_Find,
        MailOperation.Print => Translator.MailOperation_Print,
        MailOperation.ViewMessageSource => Translator.MailOperation_ViewMessageSource,
        MailOperation.RetryDraftUpload => Translator.Draft_RetryUpload,
        MailOperation.Navigate => Translator.MailOperation_Navigate,
        _ => string.Empty
    };

    /// <summary>The Wino glyph for an operation, mirroring XamlHelpers.GetWinoIconGlyph on Windows.</summary>
    public static WinoIconGlyph Glyph(MailOperation operation) => operation switch
    {
        MailOperation.Archive => WinoIconGlyph.Archive,
        MailOperation.UnArchive => WinoIconGlyph.UnArchive,
        MailOperation.SoftDelete or MailOperation.HardDelete => WinoIconGlyph.Delete,
        MailOperation.Move => WinoIconGlyph.Move,
        MailOperation.MoveToJunk or MailOperation.MarkAsNotJunk => WinoIconGlyph.Blocked,
        MailOperation.SetFlag => WinoIconGlyph.Flag,
        MailOperation.ClearFlag => WinoIconGlyph.ClearFlag,
        MailOperation.MarkAsRead => WinoIconGlyph.MarkRead,
        MailOperation.MarkAsUnread => WinoIconGlyph.MarkUnread,
        MailOperation.Ignore => WinoIconGlyph.Ignore,
        MailOperation.Reply => WinoIconGlyph.Reply,
        MailOperation.ReplyAll => WinoIconGlyph.ReplyAll,
        MailOperation.Forward => WinoIconGlyph.Forward,
        MailOperation.Zoom => WinoIconGlyph.Zoom,
        MailOperation.SaveAs or MailOperation.SaveAsPdf => WinoIconGlyph.Save,
        MailOperation.SaveAsEml or MailOperation.ViewMessageSource => WinoIconGlyph.ViewMessageSource,
        MailOperation.Print => WinoIconGlyph.Print,
        MailOperation.Find => WinoIconGlyph.Find,
        MailOperation.DarkEditor => WinoIconGlyph.DarkEditor,
        MailOperation.LightEditor => WinoIconGlyph.LightEditor,
        MailOperation.RetryDraftUpload => WinoIconGlyph.ArrowClockwise,
        MailOperation.MoveToFocused or MailOperation.AlwaysMoveToFocused => WinoIconGlyph.Important,
        MailOperation.MoveToOther or MailOperation.AlwaysMoveToOther => WinoIconGlyph.Inbox,
        _ => WinoIconGlyph.None
    };

    /// <summary>A menu or button image for the operation; null when it has no glyph.</summary>
    public static AppKit.NSImage? Image(MailOperation operation, double size = 16, AppKit.NSColor? tint = null)
        => Glyph(operation) is var glyph && glyph != WinoIconGlyph.None ? Presentation.AppKit.WinoIcons.Image(glyph, size, tint, Title(operation)) : null;
}
