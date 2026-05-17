namespace Pilgrim.EasyWorship.Options;

public sealed class EasyWorshipClientOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 9888;
    public string Uid { get; set; } = "";
    public string DeviceName { get; set; } = "ew2mqtt";
    public int DeviceType { get; set; } = 8;
    public int HeartbeatIntervalSeconds { get; set; } = 3;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int PairingTimeoutSeconds { get; set; } = 5;
    public RequestRevEncoding RequestRevEncoding { get; set; } = RequestRevEncoding.String;
    public ReconnectOptions Reconnect { get; set; } = new();

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(ConnectTimeoutSeconds);
    public TimeSpan PairingTimeout => TimeSpan.FromSeconds(PairingTimeoutSeconds);
}

public enum RequestRevEncoding
{
    String,
    Number,
}
