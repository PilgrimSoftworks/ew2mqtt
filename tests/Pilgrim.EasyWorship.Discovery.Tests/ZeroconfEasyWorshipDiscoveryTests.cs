using System.Net;
using NSubstitute;
using Pilgrim.EasyWorship.Discovery;

namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class ZeroconfEasyWorshipDiscoveryTests
{
    [Test]
    public async Task ResolveOnceAsync_returns_first_endpoint()
    {
        IZeroconfShim shim = Substitute.For<IZeroconfShim>();
        shim.ResolveAsync(IEasyWorshipDiscovery.ServiceType, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<DiscoveredService>
            {
                new("EasyWorship Live", "10.0.0.5", 9888, new[] { IPAddress.Parse("10.0.0.5") }),
                new("EasyWorship Backup", "10.0.0.6", 9888, new[] { IPAddress.Parse("10.0.0.6") }),
            });

        ZeroconfEasyWorshipDiscovery discovery = new(shim, null);
        EasyWorshipEndpoint? endpoint = await discovery.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("10.0.0.5");
        await Assert.That(endpoint.Port).IsEqualTo(9888);
        await Assert.That(endpoint.InstanceName).IsEqualTo("EasyWorship Live");
    }

    [Test]
    public async Task ResolveOnceAsync_returns_null_when_no_results()
    {
        IZeroconfShim shim = Substitute.For<IZeroconfShim>();
        shim.ResolveAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DiscoveredService>());

        ZeroconfEasyWorshipDiscovery discovery = new(shim, null);
        EasyWorshipEndpoint? endpoint = await discovery.ResolveOnceAsync(TimeSpan.FromMilliseconds(50));

        await Assert.That(endpoint).IsNull();
    }

    [Test]
    public async Task BrowseAsync_deduplicates_by_name_host_port()
    {
        IZeroconfShim shim = Substitute.For<IZeroconfShim>();
        shim.ResolveAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new List<DiscoveredService>
            {
                new("EW Live", "10.0.0.5", 9888, Array.Empty<IPAddress>()),
                new("EW Live", "10.0.0.5", 9888, Array.Empty<IPAddress>()),
                new("EW Other", "10.0.0.5", 9888, Array.Empty<IPAddress>()),
            });

        ZeroconfEasyWorshipDiscovery discovery = new(shim, null);
        List<string> seen = new();
        await foreach (EasyWorshipEndpoint ep in discovery.BrowseAsync(TimeSpan.FromMilliseconds(50)))
        {
            seen.Add(ep.InstanceName);
        }

        await Assert.That(seen.Count).IsEqualTo(2);
    }
}
