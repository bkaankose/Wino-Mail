using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// The ICS download ROPs (MS-OXCROPS 2.2.13, MS-OXCFXICS 3.2.5): configure a synchronization context
/// on a folder, hand it the client's state, drain the FastTransfer stream, ask for the new state.
/// </summary>
public static class RopSync
{
    public const byte RopFastTransferSourceGetBuffer = 0x4E;
    public const byte RopSyncConfigure = 0x70;
    public const byte RopSyncUploadStateStreamBegin = 0x75;
    public const byte RopSyncUploadStateStreamContinue = 0x76;
    public const byte RopSyncUploadStateStreamEnd = 0x77;
    public const byte RopSyncGetTransferState = 0x82;

    /// <summary>ecServerBusy: the response carries a BackoffTime and the call should be retried after it.</summary>
    public const uint ServerBusy = 0x00000480;

    public enum SynchronizationType : byte
    {
        Contents = 0x01,
        Hierarchy = 0x02,
    }

    [Flags]
    public enum SendOptions : byte
    {
        Unicode = 0x01,
        UseCpid = 0x02,
        RecoverMode = 0x04,
        ForceUnicode = 0x08,
        PartialItem = 0x10,
    }

    [Flags]
    public enum SynchronizationFlags : ushort
    {
        Unicode = 0x0001,
        NoDeletions = 0x0002,
        IgnoreNoLongerInScope = 0x0004,
        ReadState = 0x0008,
        FAI = 0x0010,
        Normal = 0x0020,
        OnlySpecifiedProperties = 0x0080,
        NoForeignIdentifiers = 0x0100,
        BestBody = 0x2000,
        IgnoreSpecifiedOnFAI = 0x4000,
        Progress = 0x8000,
    }

    [Flags]
    public enum SynchronizationExtraFlags : uint
    {
        None = 0,
        Eid = 0x00000001,
        MessageSize = 0x00000002,
        Cn = 0x00000004,
        OrderByDeliveryTime = 0x00000008,
    }

    public static byte[] BuildSyncConfigure(byte folderHandleIndex, byte outputHandleIndex, SynchronizationType type, SendOptions sendOptions,
        SynchronizationFlags flags, SynchronizationExtraFlags extraFlags, IReadOnlyList<uint> propertyTags, byte[]? restriction = null)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSyncConfigure);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(folderHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8((byte)type);
        rop.UInt8((byte)sendOptions);
        rop.UInt16((ushort)flags);
        rop.UInt16((ushort)(restriction?.Length ?? 0));
        if (restriction is { Length: > 0 })
            rop.Bytes(restriction);
        rop.UInt32((uint)extraFlags);
        rop.UInt16((ushort)propertyTags.Count);
        foreach (var tag in propertyTags)
            rop.UInt32(tag);
        return rop.ToArray();
    }

    public static void ParseSyncConfigure(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopSyncConfigure, nameof(RopSyncConfigure));

    public static byte[] BuildUploadStateStreamBegin(byte syncHandleIndex, uint stateProperty, uint totalSize)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSyncUploadStateStreamBegin);
        rop.UInt8(0);
        rop.UInt8(syncHandleIndex);
        rop.UInt32(stateProperty);
        rop.UInt32(totalSize);
        return rop.ToArray();
    }

    public static byte[] BuildUploadStateStreamContinue(byte syncHandleIndex, ReadOnlySpan<byte> data)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSyncUploadStateStreamContinue);
        rop.UInt8(0);
        rop.UInt8(syncHandleIndex);
        rop.UInt32((uint)data.Length);
        rop.Bytes(data);
        return rop.ToArray();
    }

    public static byte[] BuildUploadStateStreamEnd(byte syncHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSyncUploadStateStreamEnd);
        rop.UInt8(0);
        rop.UInt8(syncHandleIndex);
        return rop.ToArray();
    }

    /// <summary>Parses one of the three upload-state responses (they share the shape).</summary>
    public static void ParseUploadStateStream(RopReader reader, byte expectedRopId)
        => RopExecute.ExpectSuccess(reader, expectedRopId, "RopSyncUploadStateStream");

    /// <summary>RopFastTransferSourceGetBuffer: 0xBABE escapes to a 16-bit MaximumBufferSize that follows.</summary>
    public static byte[] BuildFastTransferSourceGetBuffer(byte sourceHandleIndex, ushort bufferSize)
    {
        var rop = new RopWriter();
        rop.UInt8(RopFastTransferSourceGetBuffer);
        rop.UInt8(0);
        rop.UInt8(sourceHandleIndex);
        rop.UInt16(bufferSize);
        return rop.ToArray();
    }

    public enum TransferStatus : ushort
    {
        Error = 0x0000,
        Partial = 0x0001,
        NoRoom = 0x0002,
        Done = 0x0003,
    }

    public sealed record FastTransferBuffer(TransferStatus Status, ushort InProgressCount, ushort TotalStepCount, byte[] Data, uint? BackoffMilliseconds);

    public static FastTransferBuffer ParseFastTransferSourceGetBuffer(RopReader reader)
    {
        var ropId = reader.UInt8();
        if (ropId != RopFastTransferSourceGetBuffer)
            throw new MapiFormatException($"Expected RopFastTransferSourceGetBuffer (0x4E), got 0x{ropId:X2}.");

        reader.UInt8();                  // InputHandleIndex
        var returnValue = reader.UInt32();

        // ecServerBusy carries the same response shape as success (present but empty) with a
        // BackoffTime trailing it; any other failure has no response body to read.
        var busy = returnValue == ServerBusy;
        if (returnValue != 0 && !busy)
            throw new MapiRopException(nameof(RopFastTransferSourceGetBuffer), returnValue);

        var transferStatus = (TransferStatus)reader.UInt16();
        var inProgressCount = reader.UInt16();
        var totalStepCount = reader.UInt16();
        reader.UInt8();                  // Reserved
        var bufferSize = reader.UInt16();
        var buffer = reader.Bytes(bufferSize).ToArray();
        uint? backoff = null;
        if (busy)
        {
            backoff = reader.Remaining >= 4 ? reader.UInt32() : 0u;
        }

        return new FastTransferBuffer(transferStatus, inProgressCount, totalStepCount, buffer, backoff);
    }

    public static byte[] BuildSyncGetTransferState(byte syncHandleIndex, byte outputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSyncGetTransferState);
        rop.UInt8(0);
        rop.UInt8(syncHandleIndex);
        rop.UInt8(outputHandleIndex);
        return rop.ToArray();
    }

    public static void ParseSyncGetTransferState(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopSyncGetTransferState, nameof(RopSyncGetTransferState));
}
