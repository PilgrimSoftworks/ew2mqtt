using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Discovery;
using Pilgrim.EasyWorship.Mqtt;
using Pilgrim.EasyWorship.Mqtt.Options;
using Pilgrim.EasyWorship.Options;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "EW2MQTT_");

builder.Services.AddOptions<Ew2MqttOptions>()
    .BindConfiguration(Ew2MqttOptions.SectionName)
    .ValidateOnStart();

builder.Services.AddOptions<EasyWorshipClientOptions>();

builder.Services.AddEasyWorshipClient();
builder.Services.AddEasyWorshipDiscovery();

builder.Services.AddSingleton<UidStore>();
builder.Services.AddSingleton<EndpointResolver>();
builder.Services.AddHostedService<MqttBridge>();

if (SystemdHelpers.IsSystemdService())
{
    builder.Services.AddSystemd();
}
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(o => o.ServiceName = "ew2mqtt");
}

IHost host = builder.Build();
await host.RunAsync();
