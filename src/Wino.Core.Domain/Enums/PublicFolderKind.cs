namespace Wino.Core.Domain.Enums;

/// <summary>
/// The kind of a read-only remote folder (an Exchange public folder or an online archive folder), derived
/// from its container class. Decides where the folder is surfaced: <see cref="Mail"/> opens in the mail
/// list, <see cref="Calendar"/> and <see cref="Contacts"/> are read through their own service calls, and
/// <see cref="Container"/> is a structural parent only.
/// </summary>
public enum PublicFolderKind
{
    Mail,
    Calendar,
    Contacts,
    Container,
    Other
}
