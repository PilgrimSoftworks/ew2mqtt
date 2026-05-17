using System.Text.Json;

using Pilgrim.EasyWorship.Mqtt;

namespace Pilgrim.EasyWorship.Mqtt.Tests;

// The trimmed-publish fix swapped reflection-serialized anonymous objects for a
// source-generated context. These tests pin that the on-the-wire JSON is
// byte-identical to the original anonymous shape so existing MQTT consumers
// keep working.
public sealed class MqttPayloadsTests
{
    [Test]
    public async Task State_payload_matches_legacy_anonymous_shape()
    {
        string record = JsonSerializer.Serialize(
            new StatePayload(true, false, true,
                new StateNumberRowId(2, 1001),
                new StateNumberRowId(5, 2002),
                ScheduleRev: 7, LiveRev: 9, ImageHash: "abcd", RequestRev: 42),
            ServiceJsonContext.Default.StatePayload);

        string legacy = JsonSerializer.Serialize(new
        {
            logo = true,
            black = false,
            clear = true,
            presentation = new { number = 2, rowid = 1001L },
            slide = new { number = 5, rowid = 2002L },
            schedulerev = 7L,
            liverev = 9L,
            imagehash = "abcd",
            requestrev = 42L,
        });

        await Assert.That(record).IsEqualTo(legacy);
    }

    [Test]
    public async Task Unknown_payload_keeps_null_requestrev()
    {
        string record = JsonSerializer.Serialize(
            new UnknownPayload("_malformed", "{bad}", null),
            ServiceJsonContext.Default.UnknownPayload);

        string legacy = JsonSerializer.Serialize(new { action = "_malformed", raw = "{bad}", requestrev = (long?)null });

        await Assert.That(record).IsEqualTo(legacy);
        await Assert.That(record).Contains("\"requestrev\":null");
    }

    [Test]
    public async Task Schedule_payload_matches_legacy_nested_shape()
    {
        string record = JsonSerializer.Serialize(
            new SchedulePayload(1, [new ScheduleItemPayload(1, 100, 3, "Song", [new ScheduleSlidePayload(1, 500, 4)])]),
            ServiceJsonContext.Default.SchedulePayload);

        string legacy = JsonSerializer.Serialize(new
        {
            count = 1,
            items = new[]
            {
                new
                {
                    index = 1,
                    pres_rowid = 100L,
                    revision = 3L,
                    title = "Song",
                    slides = new[] { new { index = 1, slide_rowid = 500L, revision = 4L } },
                },
            },
        });

        await Assert.That(record).IsEqualTo(legacy);
    }

    [Test]
    public async Task Heartbeat_payload_serializes_timestamp_round_trip()
    {
        DateTimeOffset ts = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        string record = JsonSerializer.Serialize(
            new HeartbeatPayload(42, ts), ServiceJsonContext.Default.HeartbeatPayload);

        string legacy = JsonSerializer.Serialize(new { requestrev = 42L, ts });

        await Assert.That(record).IsEqualTo(legacy);
    }
}
