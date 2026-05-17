using System.Net;
using System.Runtime.CompilerServices;

using NSubstitute;

namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class CompositeEasyWorshipDiscoveryTests
{
    private static ZeroconfEasyWorshipDiscovery ZeroconfWith(params DiscoveredService[] services)
    {
        IZeroconfShim shim = Substitute.For<IZeroconfShim>();
        shim.ResolveAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(services.ToList());
        return new ZeroconfEasyWorshipDiscovery(shim, null);
    }

    // Default to a probe that never connects: the resolved endpoint is then
    // returned unchanged, so these tests assert provider selection only.
    private static CompositeEasyWorshipDiscovery Composite(
        IEnumerable<IEasyWorshipDiscovery> providers, ITcpProbe? probe = null)
        => new(providers, probe ?? FakeTcpProbe.None(), null);

    [Test]
    public async Task ResolveOnceAsync_falls_back_to_zeroconf_when_first_is_empty()
    {
        StubDiscovery empty = new();
        ZeroconfEasyWorshipDiscovery zeroconf = ZeroconfWith(
            new DiscoveredService("EW Live", "10.0.0.5", 9888, [IPAddress.Parse("10.0.0.5")]));

        CompositeEasyWorshipDiscovery composite = Composite([empty, zeroconf]);
        EasyWorshipEndpoint? endpoint = await composite.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("10.0.0.5");
        await Assert.That(endpoint.InstanceName).IsEqualTo("EW Live");
    }

    [Test]
    public async Task ResolveOnceAsync_prefers_first_provider_that_yields()
    {
        StubDiscovery first = new(
            new EasyWorshipEndpoint("From Bonjour", "192.168.1.50", 9888, []));
        ZeroconfEasyWorshipDiscovery zeroconf = ZeroconfWith(
            new DiscoveredService("From Zeroconf", "10.0.0.5", 9888, []));

        CompositeEasyWorshipDiscovery composite = Composite([first, zeroconf]);
        EasyWorshipEndpoint? endpoint = await composite.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint!.InstanceName).IsEqualTo("From Bonjour");
    }

    [Test]
    public async Task ResolveOnceAsync_skips_throwing_provider()
    {
        StubDiscovery throwing = new() { Throw = true };
        ZeroconfEasyWorshipDiscovery zeroconf = ZeroconfWith(
            new DiscoveredService("EW Live", "10.0.0.5", 9888, []));

        CompositeEasyWorshipDiscovery composite = Composite([throwing, zeroconf]);
        EasyWorshipEndpoint? endpoint = await composite.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.InstanceName).IsEqualTo("EW Live");
    }

    [Test]
    public async Task BrowseAsync_falls_back_to_zeroconf_when_first_is_empty()
    {
        StubDiscovery empty = new();
        ZeroconfEasyWorshipDiscovery zeroconf = ZeroconfWith(
            new DiscoveredService("EW Live", "10.0.0.5", 9888, []));

        CompositeEasyWorshipDiscovery composite = Composite([empty, zeroconf]);
        List<string> names = new();
        await foreach (EasyWorshipEndpoint e in composite.BrowseAsync(TimeSpan.FromSeconds(1)))
        {
            names.Add(e.InstanceName);
        }

        await Assert.That(names.Count).IsEqualTo(1);
        await Assert.That(names[0]).IsEqualTo("EW Live");
    }

    [Test]
    public async Task ResolveOnceAsync_returns_null_when_all_empty()
    {
        CompositeEasyWorshipDiscovery composite = Composite([new StubDiscovery(), new StubDiscovery()]);

        await Assert.That(await composite.ResolveOnceAsync(TimeSpan.FromMilliseconds(50))).IsNull();
    }

    [Test]
    public async Task ResolveOnceAsync_selects_reachable_address_over_first_unreachable()
    {
        // A provider hands back the Tailscale address first; the LAN one in
        // Addresses is what actually listens. Composite must commit to it.
        StubDiscovery provider = new(new EasyWorshipEndpoint(
            "EasyWorship Remote (STUDIO)",
            "100.64.0.1",
            55243,
            [IPAddress.Parse("100.64.0.1"), IPAddress.Parse("192.168.10.20")]));

        CompositeEasyWorshipDiscovery composite = Composite([provider], FakeTcpProbe.Reachable("192.168.10.20"));
        EasyWorshipEndpoint? endpoint = await composite.ResolveOnceAsync(TimeSpan.FromSeconds(2));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("192.168.10.20");
        await Assert.That(endpoint.Port).IsEqualTo(55243);
        await Assert.That(endpoint.Addresses.Count).IsEqualTo(2);
    }

    private sealed class StubDiscovery : IEasyWorshipDiscovery
    {
        private readonly EasyWorshipEndpoint[] _endpoints;

        public StubDiscovery(params EasyWorshipEndpoint[] endpoints)
            => _endpoints = endpoints;

        public bool Throw { get; init; }

        public async IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("provider boom");
            }
            foreach (EasyWorshipEndpoint e in _endpoints)
            {
                await Task.Yield();
                yield return e;
            }
        }

        public async Task<EasyWorshipEndpoint?> ResolveOnceAsync(
            TimeSpan timeout, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("provider boom");
            }
            await Task.Yield();
            return _endpoints.Length > 0 ? _endpoints[0] : null;
        }
    }
}
