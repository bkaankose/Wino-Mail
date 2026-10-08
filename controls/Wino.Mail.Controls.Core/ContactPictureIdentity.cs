namespace Wino.Mail.Controls.Core;

/// <summary>
/// Immutable contact snapshot consumed by avatar controls.
/// </summary>
public sealed record ContactPictureIdentity(
    string Name,
    string Address,
    string? LocalImagePath = null) : IContactPicture;
