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
    Exchange,

    /// <summary>A local account: Calendar, People and To Do on this device, with no mail server.</summary>
    Local,
}

/// <summary>
/// Provides the host-independent information required to render an account icon.
/// </summary>
public interface IAccountIconInfo
{
    AccountIconProvider Provider { get; }

    string? ProfilePicturePath { get; }

    string? AccountColorHex { get; }

    /// <summary>A WinoIcons font character that replaces the <see cref="Provider"/> glyph, such as a catalog IMAP provider's brand mark.</summary>
    string? ProviderGlyph { get; }
}

/// <summary>
/// Immutable account icon projection for reusable controls.
/// </summary>
public sealed record AccountIconInfo(
    AccountIconProvider Provider,
    string? ProfilePicturePath = null,
    string? AccountColorHex = null,
    string? ProviderGlyph = null) : IAccountIconInfo;
