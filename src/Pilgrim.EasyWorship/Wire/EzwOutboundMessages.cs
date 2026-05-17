using System.Text.Json.Serialization;

namespace Pilgrim.EasyWorship.Wire;

internal sealed class EzwConnectCommand
{
    [JsonPropertyName("device_type")]
    public int DeviceType { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "connect";

    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";
}

internal sealed class EzwActionCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}

internal sealed class EzwGetLiveDataCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "GetLiveData";

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}

internal sealed class EzwGetScheduleDataCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "GetScheduleData";

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}

internal sealed class EzwGetSlideInfoCommand
{
    [JsonPropertyName("slide_rowid")]
    public long SlideRowId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "getSlideInfo";

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";

    [JsonPropertyName("rectype")]
    public int RecType { get; set; } = 1;

    [JsonPropertyName("pres_rowid")]
    public long PresRowId { get; set; }
}

internal sealed class EzwGotoSlideCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "gotoSlide";

    [JsonPropertyName("slide")]
    public int Slide { get; set; }

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}

internal sealed class EzwGotoScheduleCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "gotoSchedule";

    [JsonPropertyName("schedule")]
    public int Schedule { get; set; }

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}

internal sealed class EzwStatusCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "status";

    [JsonPropertyName("logo")]
    public bool Logo { get; set; }

    [JsonPropertyName("black")]
    public bool Black { get; set; }

    [JsonPropertyName("clear")]
    public bool Clear { get; set; }

    [JsonPropertyName("rectype")]
    public int RecType { get; set; }

    // EW emits pres_rowid/slide_rowid/pres_no/slide_no as quoted strings; send them
    // back as JSON numbers (Companion's typeof===number filter only stores numbers,
    // but we already parse them as longs at deserialize time).
    [JsonPropertyName("pres_rowid")]
    public long PresRowId { get; set; }

    [JsonPropertyName("slide_rowid")]
    public long SlideRowId { get; set; }

    [JsonPropertyName("pres_no")]
    public int PresNo { get; set; }

    [JsonPropertyName("slide_no")]
    public int SlideNo { get; set; }

    [JsonPropertyName("schedulerev")]
    public string ScheduleRev { get; set; } = "0";

    [JsonPropertyName("liverev")]
    public string LiveRev { get; set; } = "0";

    [JsonPropertyName("imagehash")]
    public string ImageHash { get; set; } = "";

    [JsonPropertyName("permissions")]
    public int Permissions { get; set; }

    [JsonPropertyName("requestrev")]
    public string RequestRev { get; set; } = "0";
}
