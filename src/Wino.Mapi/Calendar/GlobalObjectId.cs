using System.Security.Cryptography;

namespace Wino.Mapi.Calendar;

/// <summary>
/// PidLidGlobalObjectId (MS-OXOCAL 2.2.1.27): the identity every message about one meeting shares.
/// ByteArrayID (16 fixed bytes), YH YL M D (the exception's original date; zero for the series or a
/// single meeting), CreationTime (FILETIME), 8 reserved bytes, then a counted Data blob that makes the
/// id unique. With a zero date the clean form (PidLidCleanGlobalObjectId) is the same bytes.
/// </summary>
public static class GlobalObjectId
{
    public static readonly byte[] ByteArrayId =
    [
        0x04, 0x00, 0x00, 0x00, 0x82, 0x00, 0xE0, 0x00, 0x74, 0xC5, 0xB7, 0x10, 0x1A, 0x82, 0xE0, 0x08,
    ];

    /// <summary>Length of a freshly created id: 16 + 4 + 8 + 8 + 4 + 16 bytes of data.</summary>
    public const int CreatedLength = 56;

    /// <summary>A new id for a meeting created now; <paramref name="data"/> defaults to 16 random bytes.</summary>
    public static byte[] Create(DateTime creationUtc, byte[]? data = null)
    {
        data ??= RandomNumberGenerator.GetBytes(16);

        var id = new byte[16 + 4 + 8 + 8 + 4 + data.Length];
        ByteArrayId.CopyTo(id, 0);
        // YH YL M D stay zero: not an exception.
        BitConverter.TryWriteBytes(id.AsSpan(20, 8), DateTime.SpecifyKind(creationUtc, DateTimeKind.Utc).ToFileTimeUtc());
        // 8 reserved bytes stay zero.
        BitConverter.TryWriteBytes(id.AsSpan(36, 4), (uint)data.Length);
        data.CopyTo(id, 40);
        return id;
    }

    /// <summary>
    /// The id of one instance of a series: the master's id with the instance's original date in the
    /// YH YL M D bytes (2.2.1.27), which is how a request or cancellation names a single occurrence.
    /// </summary>
    public static byte[] ForInstance(byte[] masterId, DateTime originalDate)
    {
        var id = (byte[])masterId.Clone();
        if (id.Length < 20) return id;
        id[16] = (byte)(originalDate.Year >> 8);
        id[17] = (byte)(originalDate.Year & 0xFF);
        id[18] = (byte)originalDate.Month;
        id[19] = (byte)originalDate.Day;
        return id;
    }

    /// <summary>True when the bytes start with the fixed ByteArrayID and are long enough to carry a date and data size.</summary>
    public static bool IsWellFormed(byte[]? id)
        => id is { Length: >= 40 } && id.AsSpan(0, 16).SequenceEqual(ByteArrayId);
}
