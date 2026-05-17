using System.Text.Json.Serialization;

namespace Pilgrim.EasyWorship.Wire;

internal sealed class EzwInboundMessage
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("logo")]
    [JsonConverter(typeof(IntBooleanConverter))]
    public bool Logo { get; set; }

    [JsonPropertyName("black")]
    [JsonConverter(typeof(IntBooleanConverter))]
    public bool Black { get; set; }

    [JsonPropertyName("clear")]
    [JsonConverter(typeof(IntBooleanConverter))]
    public bool Clear { get; set; }

    [JsonPropertyName("pres_rowid")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long PresRowId { get; set; }

    [JsonPropertyName("slide_rowid")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long SlideRowId { get; set; }

    [JsonPropertyName("pres_no")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int PresNo { get; set; }

    [JsonPropertyName("slide_no")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int SlideNo { get; set; }

    [JsonPropertyName("schedulerev")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long ScheduleRev { get; set; }

    [JsonPropertyName("liverev")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long LiveRev { get; set; }

    [JsonPropertyName("imagehash")]
    public string ImageHash { get; set; } = "";

    [JsonPropertyName("rectype")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int RecType { get; set; }

    [JsonPropertyName("permissions")]
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int Permissions { get; set; }

    [JsonPropertyName("requestrev")]
    [JsonConverter(typeof(FlexibleNullableInt64Converter))]
    public long? RequestRev { get; set; }

    [JsonPropertyName("size")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long Size { get; set; }

    // slideInfo (pure JSON; no binary payload)
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
