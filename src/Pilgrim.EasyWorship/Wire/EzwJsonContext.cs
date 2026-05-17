using System.Text.Json.Serialization;

namespace Pilgrim.EasyWorship.Wire;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(EzwInboundMessage))]
[JsonSerializable(typeof(EzwConnectCommand))]
[JsonSerializable(typeof(EzwActionCommand))]
[JsonSerializable(typeof(EzwGetLiveDataCommand))]
[JsonSerializable(typeof(EzwGetScheduleDataCommand))]
[JsonSerializable(typeof(EzwGetSlideInfoCommand))]
[JsonSerializable(typeof(EzwGotoSlideCommand))]
[JsonSerializable(typeof(EzwGotoScheduleCommand))]
[JsonSerializable(typeof(EzwStatusCommand))]
internal partial class EzwJsonContext : JsonSerializerContext
{
}
