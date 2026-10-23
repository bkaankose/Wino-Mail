#nullable enable
using System;

namespace MailCal.Core.Domain.Models.LocalBackup;

/// <summary>
/// Binary layout of an encrypted backup file. Existing backups depend on every value here,
/// so change them only together with a new <see cref="CurrentFormatVersion"/>.
/// </summary>
public static class SyncSnapshotFormat
{
    public const int MagicLength = 6;

    public const ushort CurrentFormatVersion = 1;

    /// <summary>Key derived from the former online account password. Only old backups use it.</summary>
    public const byte KeySourceAccountPassword = 1;

    public const byte KeySourcePassphrase = 2;

    public const byte KdfArgon2id = 1;

    public const byte FlagGzip = 1;

    public const int FormatVersionOffset = 6;

    public const int KeySourceOffset = 8;

    public const int KdfIdOffset = 9;

    public const int MemoryKiBOffset = 10;

    public const int IterationsOffset = 14;

    public const int ParallelismOffset = 18;

    public const int FlagsOffset = 19;

    public const int SaltOffset = 20;

    public const int SaltLength = 16;

    public const int NonceOffset = 36;

    public const int NonceLength = 12;

    public const int HeaderLength = 48;

    public const int TagLength = 16;

    public const int MinPayloadLength = 65;

    public const string FileExtension = ".mailcalsnap";

    /// <summary>Extension of backups made before the Mail-Cal rename. The content format is the same.</summary>
    public const string LegacyFileExtension = ".winosnap";

    public static ReadOnlySpan<byte> Magic => "WSNAP1"u8;
}
