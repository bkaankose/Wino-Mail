namespace Wino.Mapi.Wire;

/// <summary>
/// Plain LZ77 decompression (MS-XCA section 2.1, the "DIRECT2" variant), which is what MS-OXCRPC
/// uses when RPC_HEADER_EXT sets the Compressed flag.
///
/// NOTE: it is section 2.1, NOT the LZ77+Huffman of section 2.2. The distinction is easy to get
/// wrong and cheap to check: a plain-LZ77 stream shows its literals in the clear, so UTF-16 text is
/// legible in a hex dump of the compressed bytes. A Huffman-coded stream never looks like that.
///
/// Format: a 32-bit little-endian flag word, then one item per flag bit taken from the MOST
/// significant bit downwards. A clear bit is a literal byte; a set bit is a 16-bit match descriptor
/// holding a 3-bit length and a 13-bit offset, with escapes for longer matches, including a
/// half-byte length field that is shared between two consecutive long matches.
/// </summary>
public static class XpressLz77
{
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        var output = new byte[expectedSize];
        var outputPosition = 0;
        var inputPosition = 0;

        uint bufferedFlags = 0;
        var bufferedFlagCount = 0;

        // Offset of a half-consumed length nibble, or 0 for none. Two consecutive long matches share
        // one byte: the first takes the low nibble, the second the high one.
        var lastLengthHalfByte = 0;

        while (outputPosition < expectedSize)
        {
            if (bufferedFlagCount == 0)
            {
                if (inputPosition + 4 > compressed.Length)
                {
                    throw new MapiFormatException(
                        $"Compressed stream ended while reading flags at {inputPosition} " +
                        $"({outputPosition} of {expectedSize} bytes produced).");
                }

                bufferedFlags = (uint)(compressed[inputPosition]
                    | (compressed[inputPosition + 1] << 8)
                    | (compressed[inputPosition + 2] << 16)
                    | (compressed[inputPosition + 3] << 24));
                inputPosition += 4;
                bufferedFlagCount = 32;
            }

            bufferedFlagCount--;

            if ((bufferedFlags & (1u << bufferedFlagCount)) == 0)
            {
                if (inputPosition >= compressed.Length)
                {
                    throw new MapiFormatException("Compressed stream ended mid-literal.");
                }

                output[outputPosition++] = compressed[inputPosition++];
                continue;
            }

            if (inputPosition + 2 > compressed.Length)
            {
                break;      // a truncated descriptor marks the end of the stream
            }

            var matchBytes = compressed[inputPosition] | (compressed[inputPosition + 1] << 8);
            inputPosition += 2;

            var matchLength = matchBytes % 8;
            var matchOffset = (matchBytes / 8) + 1;

            if (matchLength == 7)
            {
                // Long match: the next nibble extends the length, alternating low/high across two
                // long matches so a single byte serves both.
                if (lastLengthHalfByte == 0)
                {
                    matchLength = compressed[inputPosition] % 16;
                    lastLengthHalfByte = inputPosition;
                    inputPosition++;
                }
                else
                {
                    matchLength = compressed[lastLengthHalfByte] / 16;
                    lastLengthHalfByte = 0;
                }

                // A nibble of 15 escapes to a byte, and that byte ADDS to 15 rather than replacing
                // it. Subtracting instead produced negative intermediates for small extension bytes
                // (13 - 15 = -2), which then read back as a short match: the stream stayed valid for
                // another 80 bytes and only failed later, at an offset reaching past the output.
                if (matchLength == 15)
                {
                    matchLength = compressed[inputPosition++];

                    if (matchLength == 255)
                    {
                        matchLength = compressed[inputPosition] | (compressed[inputPosition + 1] << 8);
                        inputPosition += 2;
                        matchLength -= 15 + 7;

                        if (matchLength < 0)
                        {
                            throw new MapiFormatException("Malformed long-match length escape.");
                        }
                    }

                    matchLength += 15;
                }

                matchLength += 7;
            }

            matchLength += 3;

            if (matchOffset > outputPosition)
            {
                throw new MapiFormatException(
                    $"Match offset {matchOffset} reaches before the start of the output at {outputPosition} " +
                    $"(input offset {inputPosition}, matchBytes 0x{matchBytes:X4}, length {matchLength}). " +
                    "The stream drifted earlier than this; the length escapes are the usual cause.");
            }

            // Byte at a time on purpose: overlapping matches are normal here (a run is encoded as
            // offset 1 with a long length), so a block copy would produce the wrong bytes.
            for (var i = 0; i < matchLength && outputPosition < expectedSize; i++)
            {
                output[outputPosition] = output[outputPosition - matchOffset];
                outputPosition++;
            }
        }

        return output;
    }
}
