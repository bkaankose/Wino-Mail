namespace Wino.Core.Domain.Enums;

/// <summary>
/// Which junk sender list an address belongs to.
/// </summary>
public enum JunkListType
{
    /// <summary>Blocked senders: their mail is treated as junk.</summary>
    Blocked,

    /// <summary>Safe senders: their mail is never treated as junk.</summary>
    Safe,
}
