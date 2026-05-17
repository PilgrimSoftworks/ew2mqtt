using System.Text.Json;

using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests.Wire;

public sealed class EzwInboundMessageTests
{
    [Test]
    public async Task Status_message_with_int_booleans_and_string_requestrev_round_trips()
    {
        const string json = """
            {"action":"status","logo":0,"black":1,"clear":0,"pres_rowid":12345,"slide_rowid":67890,
             "pres_no":2,"slide_no":7,"schedulerev":42,"liverev":51,"imagehash":"abc123","requestrev":"99"}
            """;

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.Action).IsEqualTo("status");
        await Assert.That(msg.Logo).IsFalse();
        await Assert.That(msg.Black).IsTrue();
        await Assert.That(msg.Clear).IsFalse();
        await Assert.That(msg.PresRowId).IsEqualTo(12345L);
        await Assert.That(msg.SlideRowId).IsEqualTo(67890L);
        await Assert.That(msg.PresNo).IsEqualTo(2);
        await Assert.That(msg.SlideNo).IsEqualTo(7);
        await Assert.That(msg.ScheduleRev).IsEqualTo(42);
        await Assert.That(msg.LiveRev).IsEqualTo(51);
        await Assert.That(msg.ImageHash).IsEqualTo("abc123");
        await Assert.That(msg.RequestRev).IsEqualTo(99);
    }

    [Test]
    public async Task RequestRev_accepts_numeric_form()
    {
        const string json = """{"action":"heartbeat","requestrev":7}""";

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.RequestRev).IsEqualTo(7);
    }

    [Test]
    public async Task Paired_message_parses_with_minimal_fields()
    {
        const string json = """{"action":"paired"}""";

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.Action).IsEqualTo("paired");
        await Assert.That(msg.RequestRev).IsNull();
    }

    [Test]
    public async Task Size_field_is_parsed()
    {
        const string json = """{"action":"LiveData","size":1024,"requestrev":"3"}""";

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.Size).IsEqualTo(1024L);
    }

    [Test]
    public async Task SlideInfo_frame_from_real_EW_parses_title_and_content_with_stringified_rowid()
    {
        // Captured verbatim from real EW 7 in scheduled mode.
        const string json = """
            {"action":"slideInfo","rectype":1,"pres_rowid":"4","slide_rowid":"17","title":"Verse 4","reference_num":"","content":"'Tis mystery all!\r\nWho can explore","notes":"","revision":"1","label_bkcolor":"12014644","label_txcolor":"12040119"}
            """;

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.Action).IsEqualTo("slideInfo");
        await Assert.That(msg.SlideRowId).IsEqualTo(17L);
        await Assert.That(msg.PresRowId).IsEqualTo(4L);
        await Assert.That(msg.Title).IsEqualTo("Verse 4");
        await Assert.That(msg.Content).IsEqualTo("'Tis mystery all!\r\nWho can explore");
    }

    [Test]
    public async Task Revision_fields_accept_values_larger_than_Int32()
    {
        // Regression: real EW instances ship liverev/schedulerev that exceed Int.MaxValue.
        const string json = """
            {"action":"status","logo":0,"black":0,"clear":0,
             "schedulerev":12345678901234,"liverev":98765432109876,"requestrev":"3000000000"}
            """;

        EzwInboundMessage msg = JsonSerializer.Deserialize(json, EzwJsonContext.Default.EzwInboundMessage)!;

        await Assert.That(msg.ScheduleRev).IsEqualTo(12345678901234L);
        await Assert.That(msg.LiveRev).IsEqualTo(98765432109876L);
        await Assert.That(msg.RequestRev).IsEqualTo(3000000000L);
    }

    [Test]
    public async Task Connect_command_serializes_with_correct_keys()
    {
        EzwConnectCommand cmd = new()
        {
            DeviceType = 8,
            Uid = "abcd-efgh",
            DeviceName = "ew2mqtt",
        };

        string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwConnectCommand);

        await Assert.That(json).Contains("\"device_type\":8");
        await Assert.That(json).Contains("\"action\":\"connect\"");
        await Assert.That(json).Contains("\"uid\":\"abcd-efgh\"");
        await Assert.That(json).Contains("\"device_name\":\"ew2mqtt\"");
    }
}
