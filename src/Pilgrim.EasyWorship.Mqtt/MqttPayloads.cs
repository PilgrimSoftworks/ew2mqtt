using System.Text.Json.Serialization;

namespace Pilgrim.EasyWorship.Mqtt;

// Named DTOs for every MQTT JSON payload. These exist so the Service can be
// published with PublishTrimmed=true: reflection-based serialization of
// anonymous types trips IL2026 and is stripped by the trimmer. Property names
// are pinned with [JsonPropertyName] so the on-the-wire shape is identical to
// the original anonymous objects and existing consumers (Home Assistant,
// Node-RED, …) keep working.

internal sealed record ScheduleSlidePayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("slide_rowid")] long SlideRowId,
    [property: JsonPropertyName("revision")] long Revision);

internal sealed record ScheduleItemPayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("pres_rowid")] long PresRowId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("slides")] IReadOnlyList<ScheduleSlidePayload> Slides);

internal sealed record SchedulePayload(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<ScheduleItemPayload> Items);

internal sealed record PairingPayload(
    [property: JsonPropertyName("paired")] bool Paired);

internal sealed record HeartbeatPayload(
    [property: JsonPropertyName("requestrev")] long RequestRev,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts);

internal sealed record UnknownPayload(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("requestrev")] long? RequestRev);

internal sealed record PresentationSlidePayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("slide_rowid")] long SlideRowId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content);

internal sealed record PresentationSlidesPayload(
    [property: JsonPropertyName("pres_rowid")] long PresRowId,
    [property: JsonPropertyName("liverev")] long LiveRev,
    [property: JsonPropertyName("slides")] IReadOnlyList<PresentationSlidePayload> Slides);

internal sealed record PresentationLoadedPayload(
    [property: JsonPropertyName("pres_rowid")] long PresRowId,
    [property: JsonPropertyName("liverev")] long LiveRev,
    [property: JsonPropertyName("slide_count")] int SlideCount,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts);

internal sealed record SlideChangedPayload(
    [property: JsonPropertyName("slide_rowid")] long SlideRowId,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content);

internal sealed record StateNumberRowId(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("rowid")] long RowId);

internal sealed record StatePayload(
    [property: JsonPropertyName("logo")] bool Logo,
    [property: JsonPropertyName("black")] bool Black,
    [property: JsonPropertyName("clear")] bool Clear,
    [property: JsonPropertyName("presentation")] StateNumberRowId Presentation,
    [property: JsonPropertyName("slide")] StateNumberRowId Slide,
    [property: JsonPropertyName("schedulerev")] long ScheduleRev,
    [property: JsonPropertyName("liverev")] long LiveRev,
    [property: JsonPropertyName("imagehash")] string ImageHash,
    [property: JsonPropertyName("requestrev")] long RequestRev);

internal sealed record ConnectionPayload(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("paired")] bool Paired,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("lastRequestRev")] long LastRequestRev);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SchedulePayload))]
[JsonSerializable(typeof(PairingPayload))]
[JsonSerializable(typeof(HeartbeatPayload))]
[JsonSerializable(typeof(UnknownPayload))]
[JsonSerializable(typeof(PresentationSlidesPayload))]
[JsonSerializable(typeof(PresentationLoadedPayload))]
[JsonSerializable(typeof(SlideChangedPayload))]
[JsonSerializable(typeof(StatePayload))]
[JsonSerializable(typeof(ConnectionPayload))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;
