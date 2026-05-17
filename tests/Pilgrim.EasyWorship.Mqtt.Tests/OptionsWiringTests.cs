using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Options;

namespace Pilgrim.EasyWorship.Mqtt.Tests;

/// <summary>
/// Regression tests: MqttBridge resolves the EasyWorship endpoint at runtime and
/// writes Host/Port/Uid into the options. EasyWorshipClient must read those mutations.
/// If MqttBridge took IOptionsMonitor and the client took IOptions (or vice versa),
/// they would see different instances and the host would silently never reach the client.
/// </summary>
public sealed class OptionsWiringTests
{
    [Test]
    public async Task IOptions_value_is_the_same_instance_across_services()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions<EasyWorshipClientOptions>();
        services.AddEasyWorshipClient();

        await using ServiceProvider sp = services.BuildServiceProvider();

        EasyWorshipClientOptions fromOptions = sp.GetRequiredService<IOptions<EasyWorshipClientOptions>>().Value;
        EasyWorshipClientOptions fromOptionsAgain = sp.GetRequiredService<IOptions<EasyWorshipClientOptions>>().Value;

        await Assert.That(ReferenceEquals(fromOptions, fromOptionsAgain)).IsTrue();
    }

    [Test]
    public async Task Mutating_IOptions_value_is_visible_to_subsequent_readers()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions<EasyWorshipClientOptions>();
        await using ServiceProvider sp = services.BuildServiceProvider();

        EasyWorshipClientOptions writer = sp.GetRequiredService<IOptions<EasyWorshipClientOptions>>().Value;
        writer.Host = "10.0.0.5";
        writer.Port = 12345;

        EasyWorshipClientOptions reader = sp.GetRequiredService<IOptions<EasyWorshipClientOptions>>().Value;

        await Assert.That(reader.Host).IsEqualTo("10.0.0.5");
        await Assert.That(reader.Port).IsEqualTo(12345);
    }
}
