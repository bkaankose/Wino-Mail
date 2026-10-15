namespace Wino.Core.Domain.Enums;

/// <summary>When a secure item can be read and whether it may move to another device.</summary>
public enum SecureItemAccess
{
    /// <summary>The platform default (on Apple: readable while unlocked, included in encrypted backups).</summary>
    Default,

    /// <summary>
    /// Readable after the first unlock, so background work can read it, and never restored onto another device
    /// from a backup.
    /// </summary>
    ThisDeviceOnlyAfterFirstUnlock
}
