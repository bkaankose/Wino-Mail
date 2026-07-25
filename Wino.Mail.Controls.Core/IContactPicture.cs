namespace Wino.Mail.Controls.Core;

/// <summary>
/// Immutable identity of a contact used for avatar rendering.
/// Values must not change for the lifetime of the instance; the avatar
/// control resolves them exactly once per bound identity and never
/// listens for property changes.
/// </summary>
public interface IContactPicture
{
    /// <summary>
    /// Display name of the contact. Used for initials and as the color
    /// hash fallback when <see cref="Address"/> is empty.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Email address of the contact. The domain after '@' is used for
    /// favicon resolution; the full address is used for Gravatar.
    /// </summary>
    string Address { get; }

    /// <summary>
    /// Optional absolute path to a user-selected or account profile picture.
    /// This takes priority over downloaded thumbnails.
    /// </summary>
    string? LocalImagePath { get; }
}
