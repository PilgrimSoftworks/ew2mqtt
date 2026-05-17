using System.Buffers.Binary;

namespace Pilgrim.EasyWorship.Wire;

internal static class ScheduleDataParser
{
    // ScheduleData binary layout (reverse-engineered from the official EW Remote mobile
    // app's traffic against EW 7, confirmed against David's instance 2026-05-13):
    //
    //   Header (12 bytes):
    //     int32 LE                 top-level entry count
    //     byte[5]                  section header prefix (content varies)
    //     byte[3]                  `:!:` marker (0x3A 0x21 0x3A)
    //
    //   For each entry (`count` times):
    //     int64 LE                 pres_rowid
    //     int64 LE                 title_revision (use for getSlideInfo title fetch)
    //     int32 LE                 slide_count
    //     byte[5]                  inner section header prefix
    //     byte[3]                  `:!:` marker
    //     For each slide (`slide_count` times):
    //       int64 LE               slide_rowid
    //       int64 LE               revision
    public static bool TryParse(ReadOnlySpan<byte> payload, out ScheduleDataPayload result)
    {
        result = default!;
        if (payload.Length < 12)
        {
            return false;
        }

        int topCount = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        if (topCount < 0 || topCount > 1000)
        {
            return false;
        }

        int off = 12;
        List<ScheduleDataEntry> entries = new(topCount);
        for (int i = 0; i < topCount; i++)
        {
            if (payload.Length < off + 28)
            {
                return false;
            }
            long presRowId = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(off, 8));
            long revision = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(off + 8, 8));
            int slideCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(off + 16, 4));
            if (slideCount < 0 || slideCount > 10_000)
            {
                return false;
            }
            int slidesStart = off + 28;
            if (payload.Length < slidesStart + slideCount * 16)
            {
                return false;
            }
            ScheduleSlide[] slides = new ScheduleSlide[slideCount];
            for (int s = 0; s < slideCount; s++)
            {
                int soff = slidesStart + s * 16;
                long rowId = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(soff, 8));
                long rev = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(soff + 8, 8));
                slides[s] = new ScheduleSlide(rowId, rev);
            }
            entries.Add(new ScheduleDataEntry(presRowId, revision, slides));
            off = slidesStart + slideCount * 16;
        }

        result = new ScheduleDataPayload(HeaderSize: 12, ItemSize: 0, entries);
        return true;
    }
}

internal sealed record ScheduleDataPayload(int HeaderSize, int ItemSize, IReadOnlyList<ScheduleDataEntry> Entries);

internal sealed record ScheduleDataEntry(long PresRowId, long Revision, IReadOnlyList<ScheduleSlide> Slides);

internal readonly record struct ScheduleSlide(long SlideRowId, long Revision);
