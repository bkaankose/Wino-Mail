namespace Wino.Core.Domain.Enums;

/// <summary>
/// Which picture store a file belongs to. Each kind has its own subfolder and file name format.
/// </summary>
public enum PictureKind
{
    /// <summary>Contact pictures. Raw bytes under contacts/{guid}.jpg.</summary>
    Contact,

    /// <summary>Account profile pictures. Normalized to 48x48 under account-profile-pictures/{guid:N}.jpg.</summary>
    AccountProfile
}
