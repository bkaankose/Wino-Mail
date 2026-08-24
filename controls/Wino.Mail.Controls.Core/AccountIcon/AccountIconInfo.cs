namespace Wino.Mail.Controls.Core.AccountIcon;

/// <summary>
/// Identifies the provider glyph used when an account profile picture is unavailable or disabled.
/// </summary>
public enum AccountIconProvider
{
    Microsoft,
    Google,
    ICloud,
    Yahoo,
    Imap,
}

/// <summary>
/// Provides the host-independent information required to render an account icon.
/// </summary>
public interface IAccountIconInfo
{
    AccountIconProvider Provider { get; }

    string? ProfilePicturePath { get; }

    string? AccountColorHex { get; }
}

/// <summary>
/// Immutable account icon projection for reusable controls.
/// </summary>
public sealed record AccountIconInfo(
    AccountIconProvider Provider,
    string? ProfilePicturePath = null,
    string? AccountColorHex = null) : IAccountIconInfo;
