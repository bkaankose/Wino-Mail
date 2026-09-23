using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.Controls.AccountIcon;

internal static class AccountIconGlyphs
{
    public static string GetGlyph(AccountIconProvider provider) => provider switch
    {
        AccountIconProvider.Microsoft => WinoIconCodes.Microsoft,
        AccountIconProvider.Google => WinoIconCodes.Google,
        AccountIconProvider.ICloud => WinoIconCodes.Apple,
        AccountIconProvider.Yahoo => WinoIconCodes.Yahoo,
        AccountIconProvider.Imap => WinoIconCodes.IMAP,
        _ => WinoIconCodes.IMAP,
    };
}
