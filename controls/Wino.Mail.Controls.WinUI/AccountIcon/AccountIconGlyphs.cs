using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.Controls.AccountIcon;

internal static class AccountIconGlyphs
{
    /// <summary>
    /// Microsoft and Google are multi-color glyphs that ignore Foreground.
    /// A tinted icon (the account has its own color) uses their monochrome twins instead.
    /// </summary>
    public static string GetGlyph(AccountIconProvider provider, bool isTinted) => provider switch
    {
        AccountIconProvider.Microsoft => isTinted ? WinoIconCodes.MicrosoftMono : WinoIconCodes.Microsoft,
        AccountIconProvider.Google => isTinted ? WinoIconCodes.GoogleMono : WinoIconCodes.Google,
        AccountIconProvider.ICloud => WinoIconCodes.Apple,
        AccountIconProvider.Yahoo => WinoIconCodes.Yahoo,
        AccountIconProvider.Imap => WinoIconCodes.IMAP,
        _ => WinoIconCodes.IMAP,
    };
}
