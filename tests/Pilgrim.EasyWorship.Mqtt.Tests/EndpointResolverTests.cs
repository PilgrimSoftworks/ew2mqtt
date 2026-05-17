using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pilgrim.EasyWorship.Discovery;
using Pilgrim.EasyWorship.Mqtt;
using Pilgrim.EasyWorship.Mqtt.Options;

namespace Pilgrim.EasyWorship.Mqtt.Tests;

public sealed class EndpointResolverTests
{
    private static EndpointResolver Resolver(IEasyWorshipDiscovery discovery)
        => new(discovery, NullLogger<EndpointResolver>.Instance);

    private static IEasyWorshipDiscovery DiscoveryReturning(EasyWorshipEndpoint? endpoint)
    {
        IEasyWorshipDiscovery d = Substitute.For<IEasyWorshipDiscovery>();
        d.ResolveOnceAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(endpoint));
        return d;
    }

    private static EasyWorshipEndpoint Endpoint(string host, int port)
        => new("EasyWorship Remote (STUDIO)", host, port, Array.Empty<IPAddress>());

    [Test]
    public async Task Manual_with_host_and_explicit_port_returns_it()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.Manual,
            Host = "10.0.0.5",
            Port = 50123,
        };

        (string Host, int Port)? result = await resolver.ResolveAsync(options, default);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Host).IsEqualTo("10.0.0.5");
        await Assert.That(result.Value.Port).IsEqualTo(50123);
    }

    [Test]
    public async Task Manual_with_host_but_no_port_returns_null()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.Manual,
            Host = "10.0.0.5",
            Port = null,
        };

        await Assert.That(await resolver.ResolveAsync(options, default)).IsNull();
    }

    [Test]
    public async Task Manual_with_invalid_port_returns_null()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.Manual,
            Host = "10.0.0.5",
            Port = 0,
        };

        await Assert.That(await resolver.ResolveAsync(options, default)).IsNull();
    }

    [Test]
    public async Task Auto_uses_discovered_endpoint()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(Endpoint("100.64.0.1", 55243)));
        EasyWorshipServiceOptions options = new() { DiscoveryMode = DiscoveryMode.Auto };

        (string Host, int Port)? result = await resolver.ResolveAsync(options, default);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Host).IsEqualTo("100.64.0.1");
        await Assert.That(result.Value.Port).IsEqualTo(55243);
    }

    [Test]
    public async Task Auto_returns_null_when_discovery_finds_nothing()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new() { DiscoveryMode = DiscoveryMode.Auto };

        await Assert.That(await resolver.ResolveAsync(options, default)).IsNull();
    }

    [Test]
    public async Task AutoThenManual_prefers_discovery()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(Endpoint("100.64.0.1", 55243)));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.AutoThenManual,
            Host = "10.0.0.5",
            Port = 50123,
        };

        (string Host, int Port)? result = await resolver.ResolveAsync(options, default);

        await Assert.That(result!.Value.Host).IsEqualTo("100.64.0.1");
        await Assert.That(result.Value.Port).IsEqualTo(55243);
    }

    [Test]
    public async Task AutoThenManual_falls_back_to_manual_when_discovery_empty()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.AutoThenManual,
            Host = "10.0.0.5",
            Port = 50123,
        };

        (string Host, int Port)? result = await resolver.ResolveAsync(options, default);

        await Assert.That(result!.Value.Host).IsEqualTo("10.0.0.5");
        await Assert.That(result.Value.Port).IsEqualTo(50123);
    }

    [Test]
    public async Task AutoThenManual_no_fallback_without_explicit_port()
    {
        EndpointResolver resolver = Resolver(DiscoveryReturning(null));
        EasyWorshipServiceOptions options = new()
        {
            DiscoveryMode = DiscoveryMode.AutoThenManual,
            Host = "10.0.0.5",
            Port = null,
        };

        await Assert.That(await resolver.ResolveAsync(options, default)).IsNull();
    }
}
