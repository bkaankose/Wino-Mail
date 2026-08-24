using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.Controls.AccountIcon;

internal static class AccountIconGlyphs
{
    public static string GetGlyph(AccountIconProvider provider) => provider switch
    {
        AccountIconProvider.Microsoft => "\uE904",
        AccountIconProvider.Google => "\uE905",
        AccountIconProvider.ICloud => "\uE92B",
        AccountIconProvider.Yahoo => "\uE92C",
        AccountIconProvider.Imap => "\uE715",
        _ => "\uE715",
    };
}
