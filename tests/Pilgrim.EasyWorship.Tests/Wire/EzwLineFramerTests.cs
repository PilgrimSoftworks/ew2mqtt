using System.Buffers;
using System.Text;

using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests.Wire;

public sealed class EzwLineFramerTests
{
    [Test]
    public async Task TryReadLine_returns_false_when_no_terminator()
    {
        ReadOnlySequence<byte> buffer = ToSequence("hello world without crlf");

        bool ok = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line);

        await Assert.That(ok).IsFalse();
        await Assert.That(line.Length).IsEqualTo(0);
        await Assert.That(buffer.Length).IsEqualTo(24);
    }

    [Test]
    public async Task TryReadLine_returns_single_line_and_advances_buffer()
    {
        ReadOnlySequence<byte> buffer = ToSequence("first\r\nsecond\r\n");

        bool first = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line1);
        bool second = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line2);
        bool third = EzwLineFramer.TryReadLine(ref buffer, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(line1.ToArray())).IsEqualTo("first");
        await Assert.That(second).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(line2.ToArray())).IsEqualTo("second");
        await Assert.That(third).IsFalse();
    }

    [Test]
    public async Task TryReadLine_handles_empty_lines()
    {
        ReadOnlySequence<byte> buffer = ToSequence("\r\nfoo\r\n");

        bool ok1 = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line1);
        bool ok2 = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line2);

        await Assert.That(ok1).IsTrue();
        await Assert.That(line1.Length).IsEqualTo(0);
        await Assert.That(ok2).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(line2.ToArray())).IsEqualTo("foo");
    }

    [Test]
    public async Task TryReadLine_handles_multi_segment_buffers()
    {
        ReadOnlySequenceSegment<byte>[] bufferList = new ReadOnlySequenceSegment<byte>[3];
        MemorySegment<byte> seg1 = new(Encoding.UTF8.GetBytes("hel"));
        MemorySegment<byte> seg2 = seg1.Append(Encoding.UTF8.GetBytes("lo\r"));
        MemorySegment<byte> seg3 = seg2.Append(Encoding.UTF8.GetBytes("\nworld"));
        ReadOnlySequence<byte> buffer = new(seg1, 0, seg3, seg3.Memory.Length);

        bool ok = EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line);

        await Assert.That(ok).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(line.ToArray())).IsEqualTo("hello");
        await Assert.That(Encoding.UTF8.GetString(buffer.ToArray())).IsEqualTo("world");
    }

    private static ReadOnlySequence<byte> ToSequence(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    private sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
    {
        public MemorySegment(ReadOnlyMemory<T> memory)
        {
            Memory = memory;
        }

        public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
        {
            MemorySegment<T> seg = new(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = seg;
            return seg;
        }
    }
}
