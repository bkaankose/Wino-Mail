using Wino.Core.Domain.Models.Accounts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>The text and icon colour of an account group for the current light or dark appearance; null for the default colour.</summary>
internal static class AccountGroupTint
{
    public static string? Hex(string? colorKey)
        => AccountGroupColors.Find(colorKey) is { } color ? (WinoAppearance.IsDark ? color.DarkHex : color.LightHex) : null;
}
