using Pilgrim.EasyWorship.Options;

namespace Pilgrim.EasyWorship.Mqtt.Options;

public sealed class Ew2MqttOptions
{
    public const string SectionName = "Ew2Mqtt";

    public string InstanceId { get; set; } = "default";
    public EasyWorshipServiceOptions EasyWorship { get; set; } = new();
    public MqttOptions Mqtt { get; set; } = new();
}

public sealed class EasyWorshipServiceOptions
{
    public DiscoveryMode DiscoveryMode { get; set; } = DiscoveryMode.AutoThenManual;
    public string? Host { get; set; }

    /// <summary>
    /// Manual TCP port. There is intentionally no default: EasyWorship binds a
    /// <em>dynamic</em> ezwremote port, so manual mode needs an explicit value
    /// (otherwise use Auto discovery, which learns the real port).
    /// </summary>
    public int? Port { get; set; }
    public string? Uid { get; set; }
    public string DeviceName { get; set; } = "ew2mqtt";
    public int DeviceType { get; set; } = 8;
    public int HeartbeatIntervalSeconds { get; set; } = 3;
    public RequestRevEncoding RequestRevEncoding { get; set; } = RequestRevEncoding.String;
    public int DiscoveryTimeoutSeconds { get; set; } = 5;
    public ReconnectOptions Reconnect { get; set; } = new();
}

public enum DiscoveryMode
{
    Manual,
    Auto,
    AutoThenManual,
}

public sealed class MqttOptions
{
    public string Server { get; set; } = "mqtt://localhost:1883";
    public string ClientId { get; set; } = "ew2mqtt-default";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Tls { get; set; }
    public string TopicBase { get; set; } = "ew2mqtt";
    public bool RetainState { get; set; } = true;
    public bool HomeAssistantDiscovery { get; set; }
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public int KeepAliveSeconds { get; set; } = 30;
    public int ReconnectIntervalSeconds { get; set; } = 5;
}
