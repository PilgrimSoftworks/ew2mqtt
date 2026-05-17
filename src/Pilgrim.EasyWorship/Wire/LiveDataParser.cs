using System.Buffers.Binary;

namespace Pilgrim.EasyWorship.Wire;

internal static class LiveDataParser
{
    private const int HeaderSize = 40;
    private const int SlideRecordSize = 16;

    public static bool TryParse(ReadOnlySpan<byte> payload, out LiveDataPayload result)
    {
        result = default!;
        if (payload.Length < HeaderSize)
        {
            return false;
        }

        // Header: <lqqqlq>
        // 0..3   int32   unknown0
        // 4..11  int64   liverev
        // 12..19 int64   pres_rowid
        // 20..27 int64   title_revision
        // 28..31 int32   pres_len
        // 32..39 int64   unknown5
        long liveRev = BinaryPrimitives.ReadInt64LittleEndian(payload[4..12]);
        long presRowId = BinaryPrimitives.ReadInt64LittleEndian(payload[12..20]);
        long titleRevision = BinaryPrimitives.ReadInt64LittleEndian(payload[20..28]);
        int presLen = BinaryPrimitives.ReadInt32LittleEndian(payload[28..32]);

        // Bound the count before it drives an allocation. Without this, a
        // malformed/hostile presLen makes `presLen * SlideRecordSize` overflow
        // Int32 (negative `expected` slips past the length check) and
        // `new LiveDataSlide[presLen]` then triggers an OOM that faults the
        // connection. 10_000 mirrors ScheduleDataParser's per-entry slide cap.
        if (presLen is < 0 or > 10_000)
        {
            return false;
        }

        long expected = HeaderSize + ((long)presLen * SlideRecordSize);
        if (payload.Length < expected)
        {
            return false;
        }

        LiveDataSlide[] slides = new LiveDataSlide[presLen];
        for (int i = 0; i < presLen; i++)
        {
            int offset = HeaderSize + (i * SlideRecordSize);
            slides[i] = new LiveDataSlide(
                SlideRowId: BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8)),
                Revision: BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset + 8, 8)));
        }

        result = new LiveDataPayload(liveRev, presRowId, titleRevision, slides);
        return true;
    }
}

internal sealed record LiveDataPayload(
    long LiveRev,
    long PresRowId,
    long TitleRevision,
    IReadOnlyList<LiveDataSlide> Slides);

internal readonly record struct LiveDataSlide(long SlideRowId, long Revision);
