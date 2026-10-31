using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// The Execute request (MS-OXCMAPIHTTP 2.2.4.2) and the two layers of framing it carries.
///
/// A ROP does not go on the wire on its own. It is wrapped as:
///
///   Execute body   Flags | RopBufferSize | RopBuffer | MaxRopOut | AuxBufferSize
///   RopBuffer      RPC_HEADER_EXT | RopSize | ROPs... | ServerObjectHandleTable
///
/// The two easy mistakes are both in the sizes: RopSize counts itself, and RPC_HEADER_EXT's Size
/// covers what follows it. Get either wrong and the server answers with a transport-level error
/// rather than anything about the ROP.
/// </summary>
public static class RopExecute
{
    /// <summary>Server object handle meaning "no handle yet", which a Logon starts from.</summary>
    public const uint NullHandle = 0xFFFFFFFF;

    /// <summary>
    /// Exchange caps the ROP response buffer at 32KB including headers (a larger request fails the
    /// whole Execute with ecBufferTooSmall). Callers that read streams should stay well under it.
    /// </summary>
    public const int MaxRopResponseBytes = 32 * 1024;

    public static byte[] BuildExecuteBody(byte[] ropBytes, IReadOnlyList<uint> handleTable, uint maxRopOut = 262144)
    {
        var ropBuffer = BuildRopBuffer(ropBytes, handleTable);

        var body = new RopWriter();
        body.UInt32(0);                          // Flags (unused, must be 0)
        body.UInt32((uint)ropBuffer.Length);     // RopBufferSize
        body.Bytes(ropBuffer);                   // RopBuffer
        body.UInt32(maxRopOut);                  // MaxRopOut
        body.UInt32(0);                          // AuxiliaryBufferSize

        return body.ToArray();
    }

    private static byte[] BuildRopBuffer(byte[] ropBytes, IReadOnlyList<uint> handleTable)
    {
        // RopSize counts the 2 bytes of RopSize itself, then the ROPs. The handle table follows and
        // is NOT counted by RopSize; it is found by reading to the end of the buffer.
        var payload = new RopWriter();
        payload.UInt16((ushort)(ropBytes.Length + 2));
        payload.Bytes(ropBytes);
        foreach (var handle in handleTable)
        {
            payload.UInt32(handle);
        }

        var payloadBytes = payload.ToArray();

        var framed = new RopWriter();
        framed.UInt16(0x0000);                      // Version
        framed.UInt16(0x0004);                      // Flags: Last
        framed.UInt16((ushort)payloadBytes.Length); // Size
        framed.UInt16((ushort)payloadBytes.Length); // SizeActual (equal when not compressed)
        framed.Bytes(payloadBytes);

        return framed.ToArray();
    }

    /// <summary>
    /// Unwraps an Execute response body back down to the ROP bytes and the handle table.
    /// </summary>
    public static (byte[] Rops, uint[] Handles) ParseExecuteResponse(byte[] body)
    {
        var reader = new RopReader(body);

        var statusCode = reader.UInt32();
        if (statusCode != 0)
        {
            throw new MapiFormatException($"Execute returned StatusCode 0x{statusCode:X8}.");
        }

        // ErrorCode is the RPC-level verdict on the whole Execute, separate from any ROP's ReturnValue.
        // When it is set, what follows is not a ROP buffer at all but a diagnostic (Exchange puts the
        // exception type name in it), and reading it as ROPs fails somewhere meaningless.
        var errorCode = reader.UInt32();
        if (errorCode != 0)
        {
            throw new MapiRpcException(errorCode);
        }

        reader.UInt32();                            // Flags
        var ropBufferSize = reader.UInt32();
        var ropBuffer = reader.Bytes((int)ropBufferSize);

        var header = new RopReader(ropBuffer);
        header.UInt16();                            // Version
        var headerFlags = header.UInt16();
        var size = header.UInt16();
        var sizeActual = header.UInt16();

        var payload = header.Bytes(Math.Min(size, header.Remaining)).ToArray();

        // RPC_HEADER_EXT flags: 0x0001 Compressed, 0x0002 XorMagic, 0x0004 Last.
        //
        // Exchange sets XorMagic on responses, which obfuscates everything after the header by
        // XOR-ing each byte with 0xA5. Skip this and the buffer still parses, into nonsense: the
        // first field read comes back as 42253 rather than 168, because 0xA8 0x00 arrives as
        // 0x0D 0xA5. The giveaway in a hex dump is long runs of 0xA5, which is what zero-filled
        // regions turn into.
        if ((headerFlags & 0x0002) != 0)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= 0xA5;
            }
        }

        // Compressed comes AFTER XorMagic on the way in, because the sender obfuscates last.
        // SizeActual is the uncompressed length; Size is what arrived.
        if ((headerFlags & 0x0001) != 0)
        {
            payload = XpressLz77.Decompress(payload, sizeActual);
        }

        var inner = new RopReader(payload);
        var ropSize = inner.UInt16();
        var rops = inner.Bytes(ropSize - 2).ToArray();

        // Whatever follows the ROPs is the handle table; RopSize does not count it.
        var handleCount = inner.Remaining / 4;
        var handles = new uint[Math.Max(0, handleCount)];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = inner.UInt32();
        }

        return (rops, handles);
    }

    /// <summary>
    /// Reads a ROP response header (RopId, handle index, ReturnValue) and throws if it is not the
    /// expected ROP or it failed. Every response parser starts here.
    /// </summary>
    public static void ExpectSuccess(RopReader reader, byte expectedRopId, string name)
    {
        var ropId = reader.UInt8();
        if (ropId != expectedRopId)
        {
            throw new MapiFormatException($"Expected {name} (0x{expectedRopId:X2}) in the response, got 0x{ropId:X2}.");
        }

        reader.UInt8();                  // handle index
        var returnValue = reader.UInt32();
        if (returnValue != 0)
        {
            // A failed ROP normally ends here, but the server sometimes appends more (a partial
            // response, or diagnostics); keep it on the exception so a failure can be read later.
            var trailing = reader.Remaining > 0 ? HexDump.Render(reader.Bytes(reader.Remaining).Span, 1536) : null;
            throw new MapiRopException(name, returnValue, trailing);
        }
    }
}
