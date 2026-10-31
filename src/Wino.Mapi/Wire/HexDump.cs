using System.Text;

namespace Wino.Mapi.Wire;

/// <summary>
/// Hex rendering for diagnostics. In this protocol the bytes are the only reliable witness: a wrong
/// read usually produces a plausible number rather than an error, so a dump is what lets a value be
/// checked against what the server actually sent. Callers decide where it goes. The library never
/// logs a buffer itself, because a buffer may contain mailbox content.
/// </summary>
public static class HexDump
{
    public static string Render(ReadOnlySpan<byte> data, int limit = 512)
    {
        var length = Math.Min(data.Length, limit);
        var builder = new StringBuilder();

        for (var offset = 0; offset < length; offset += 16)
        {
            var slice = data.Slice(offset, Math.Min(16, length - offset));
            var hex = new StringBuilder(slice.Length * 3);
            var text = new StringBuilder(slice.Length);
            foreach (var b in slice)
            {
                hex.Append(b.ToString("x2")).Append(' ');
                text.Append(b >= 32 && b < 127 ? (char)b : '.');
            }

            builder.Append("  ").Append(offset.ToString("x4")).Append("  ")
                   .Append(hex.ToString().TrimEnd().PadRight(47)).Append("  ")
                   .Append(text).AppendLine();
        }

        if (data.Length > length)
        {
            builder.Append("  ... ").Append(data.Length - length).Append(" more bytes").AppendLine();
        }

        return builder.ToString();
    }
}
