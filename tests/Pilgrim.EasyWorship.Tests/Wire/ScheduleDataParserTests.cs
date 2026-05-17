using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests.Wire;

public sealed class ScheduleDataParserTests
{
    // Golden payload captured from EW 7 — schedule had 3 entries:
    //   pres_rowid 1 ("05 Ditt navn…")  — 7 child slides
    //   pres_rowid 3 ("45 O Herre Krist…") — 6 child slides
    //   pres_rowid 4 ("41 Med bønn jeg…")  — 6 child slides
    private const string GoldenHex =
        "0300000003085E88113A213A010000000000000002A48F691E97F8390700000003084F88113A213A" +
        "020000000000000002A490691E97F839030000000000000002A490691E97F839040000000000000002A490691E97F839" +
        "050000000000000002A490691E97F839060000000000000002A490691E97F839070000000000000002A490691E97F839" +
        "080000000000000002A490691E97F839" +
        "030000000000000002A48F6C1E97F8390600000003085688113A213A" +
        "090000000000000002A4906C1E97F8390A0000000000000002A4906C1E97F8390B0000000000000002A4906C1E97F839" +
        "0C0000000000000002A4906C1E97F8390D0000000000000002A4906C1E97F8390E0000000000000002A4906C1E97F839" +
        "040000000000000002A4376C1E97F8390600000003085D88113A213A" +
        "0F0000000000000002A4386C1E97F839100000000000000002A4386C1E97F839110000000000000002A4386C1E97F839" +
        "120000000000000002A4386C1E97F839130000000000000002A4386C1E97F839140000000000000002A4386C1E97F839";

    [Test]
    public async Task Parses_real_EW_payload_into_three_entries()
    {
        byte[] payload = Convert.FromHexString(GoldenHex);
        await Assert.That(payload.Length).IsEqualTo(400);

        bool ok = ScheduleDataParser.TryParse(payload, out ScheduleDataPayload? result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.Entries.Count).IsEqualTo(3);

        await Assert.That(result.Entries[0].PresRowId).IsEqualTo(1L);
        await Assert.That(result.Entries[0].Revision).IsEqualTo(4177254811261969410L);

        await Assert.That(result.Entries[1].PresRowId).IsEqualTo(3L);
        await Assert.That(result.Entries[1].Revision).IsEqualTo(4177254811312301058L);

        await Assert.That(result.Entries[2].PresRowId).IsEqualTo(4L);
        await Assert.That(result.Entries[2].Revision).IsEqualTo(4177254811306533890L);
    }

    [Test]
    public async Task Returns_false_on_short_payload()
    {
        bool ok = ScheduleDataParser.TryParse(new byte[8], out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Returns_false_when_entry_count_is_unreasonable()
    {
        // 4-byte int32 LE = 0xFFFFFFFF — implausible count
        byte[] bytes = new byte[12];
        bytes[0] = 0xFF; bytes[1] = 0xFF; bytes[2] = 0xFF; bytes[3] = 0x7F;
        bool ok = ScheduleDataParser.TryParse(bytes, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Returns_false_when_slide_count_overruns_payload()
    {
        // 1 top entry; entry header claims slide_count=1000 but payload is too short.
        byte[] bytes = new byte[12 + 28];
        bytes[0] = 1; // top count
        // entry header at offset 12: 16 zero bytes + slide_count = 1000 + 8 bytes of section/marker
        bytes[12 + 16] = 0xE8; bytes[12 + 17] = 0x03; // 0x03E8 = 1000 LE
        bool ok = ScheduleDataParser.TryParse(bytes, out _);
        await Assert.That(ok).IsFalse();
    }
}
