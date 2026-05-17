using System.Buffers.Binary;

using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests.Wire;

public sealed class LiveDataParserTests
{
    [Test]
    public async Task Parses_header_and_slide_records_in_little_endian()
    {
        byte[] payload = BuildLiveData(
            liveRev: 0x1122_3344_5566_7788,
            presRowId: 0x0102_0304_0506_0708,
            titleRevision: 0x00AA_BB00_00CC_DD00,
            slides: new (long, long)[]
            {
                (-100165, 11),
                (-100164, 12),
                (-100163, 13),
            });

        bool ok = LiveDataParser.TryParse(payload, out LiveDataPayload? result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.LiveRev).IsEqualTo(0x1122_3344_5566_7788);
        await Assert.That(result.PresRowId).IsEqualTo(0x0102_0304_0506_0708);
        await Assert.That(result.TitleRevision).IsEqualTo(0x00AA_BB00_00CC_DD00);
        await Assert.That(result.Slides.Count).IsEqualTo(3);
        await Assert.That(result.Slides[0].SlideRowId).IsEqualTo(-100165L);
        await Assert.That(result.Slides[1].Revision).IsEqualTo(12L);
        await Assert.That(result.Slides[2].SlideRowId).IsEqualTo(-100163L);
    }

    [Test]
    public async Task Returns_false_when_payload_smaller_than_header()
    {
        bool ok = LiveDataParser.TryParse(new byte[20], out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Returns_false_when_payload_truncated_in_slide_list()
    {
        // Header advertises 3 slides but only 2 records of 16 bytes follow.
        byte[] payload = BuildLiveData(
            liveRev: 1, presRowId: 2, titleRevision: 3,
            slides: new (long, long)[] { (10, 11), (12, 13), (14, 15) });
        byte[] truncated = payload.AsSpan(0, payload.Length - 8).ToArray();

        bool ok = LiveDataParser.TryParse(truncated, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Handles_empty_slide_list()
    {
        byte[] payload = BuildLiveData(
            liveRev: 100, presRowId: 200, titleRevision: 300,
            slides: Array.Empty<(long, long)>());

        bool ok = LiveDataParser.TryParse(payload, out LiveDataPayload? result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.Slides.Count).IsEqualTo(0);
    }

    private static byte[] BuildLiveData(long liveRev, long presRowId, long titleRevision, (long Rowid, long Rev)[] slides)
    {
        byte[] bytes = new byte[40 + slides.Length * 16];
        // unknown0 (4) — leave 0
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4, 8), liveRev);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12, 8), presRowId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(20, 8), titleRevision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), slides.Length);
        // unknown5 (8) at offset 32 — leave 0
        for (int i = 0; i < slides.Length; i++)
        {
            int off = 40 + i * 16;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(off, 8), slides[i].Rowid);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(off + 8, 8), slides[i].Rev);
        }
        return bytes;
    }
}
