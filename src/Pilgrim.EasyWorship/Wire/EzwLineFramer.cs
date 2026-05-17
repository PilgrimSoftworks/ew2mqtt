using System.Buffers;

namespace Pilgrim.EasyWorship.Wire;

internal static class EzwLineFramer
{
    public const long MaxFrameSize = 1L * 1024 * 1024;

    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    public static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequenceReader<byte> reader = new(buffer);
        if (reader.TryReadTo(out line, Crlf))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        line = default;
        return false;
    }
}
