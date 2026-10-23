using MailCal.Mail.Controls.Core.AccountIcon;

namespace MailCal.Mail.Controls.AccountIcon;

internal static class AccountIconGlyphs
{
    /// <summary>
    /// Microsoft and Google are multi-color glyphs that ignore Foreground.
    /// A tinted icon (the account has its own color) uses their monochrome twins instead.
    /// </summary>
    public static string GetGlyph(AccountIconProvider provider, bool isTinted) => provider switch
    {
        AccountIconProvider.Microsoft => isTinted ? MailCalIconCodes.MicrosoftMono : MailCalIconCodes.Microsoft,
        AccountIconProvider.Google => isTinted ? MailCalIconCodes.GoogleMono : MailCalIconCodes.Google,
        AccountIconProvider.ICloud => MailCalIconCodes.Apple,
        AccountIconProvider.Yahoo => MailCalIconCodes.Yahoo,
        AccountIconProvider.Imap => MailCalIconCodes.IMAP,
        _ => MailCalIconCodes.IMAP,
    };
}
