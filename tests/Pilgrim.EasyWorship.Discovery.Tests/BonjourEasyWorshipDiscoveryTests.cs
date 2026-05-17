
namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class BonjourEasyWorshipDiscoveryTests
{
    private const string BrowseCapture =
        "Browsing for _ezwremote._tcp\n" +
        "Timestamp  A/R Flags if Domain   Service Type        Instance Name\n" +
        " 7:18:51.660 Add  3 56 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n" +
        " 7:18:51.660 Add  3 41 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n" +
        " 7:18:51.660 Add  2 23 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n";

    private const string ResolveCapture =
        " 7:20:01.456  EasyWorship\\032Remote\\032(STUDIO)._ezwremote._tcp.local. " +
        "can be reached at ew-host.local.:9888 (interface 56)\n";

    private const string AddressCapture =
        " 7:20:10.111 Add  2 56 ew-host.local.  192.168.1.50  120\n";

    private static FakeProcessRunner Runner(Func<string, ProcessRunResult> byVerb)
        => new(args => byVerb(args.Count > 0 ? args[0] : string.Empty));

    private static BonjourEasyWorshipDiscovery Discovery(FakeProcessRunner runner)
        => new("dns-sd", runner, null);

    [Test]
    public async Task ResolveOnceAsync_returns_endpoint_with_ip_host_from_G()
    {
        FakeProcessRunner runner = Runner(verb => verb switch
        {
            "-B" => FakeProcessRunner.Streamed(BrowseCapture),
            "-L" => FakeProcessRunner.Streamed(ResolveCapture),
            "-G" => FakeProcessRunner.Streamed(AddressCapture),
            _ => FakeProcessRunner.Empty(),
        });

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromSeconds(2));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.InstanceName).IsEqualTo("EasyWorship Remote (STUDIO)");
        await Assert.That(endpoint.Host).IsEqualTo("192.168.1.50");
        await Assert.That(endpoint.Port).IsEqualTo(9888);
        await Assert.That(endpoint.Addresses.Count).IsEqualTo(1);
        await Assert.That(endpoint.Addresses[0].ToString()).IsEqualTo("192.168.1.50");
    }

    [Test]
    public async Task ResolveOnceAsync_surfaces_all_G_addresses_with_first_as_host()
    {
        // Multi-homed host: -G answers one row per interface. All must reach
        // endpoint.Addresses so reachability selection has every candidate.
        const string multiAddress =
            " 7:20:10.111 Add  2 56 ew-host.local.  100.64.0.1  120\n" +
            " 7:20:10.222 Add  2 41 ew-host.local.  192.168.10.20   120\n" +
            " 7:20:10.333 Add  2 23 ew-host.local.  192.168.10.20   120\n";

        FakeProcessRunner runner = Runner(verb => verb switch
        {
            "-B" => FakeProcessRunner.Streamed(BrowseCapture),
            "-L" => FakeProcessRunner.Streamed(ResolveCapture),
            "-G" => FakeProcessRunner.Streamed(multiAddress),
            _ => FakeProcessRunner.Empty(),
        });

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromSeconds(2));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("100.64.0.1"); // first, unchanged
        await Assert.That(endpoint.Addresses.Count).IsEqualTo(2);
        await Assert.That(endpoint.Addresses[0].ToString()).IsEqualTo("100.64.0.1");
        await Assert.That(endpoint.Addresses[1].ToString()).IsEqualTo("192.168.10.20");
    }

    [Test]
    public async Task BrowseAsync_dedupes_instance_across_interfaces()
    {
        FakeProcessRunner runner = Runner(verb => verb switch
        {
            "-B" => FakeProcessRunner.Streamed(BrowseCapture),
            "-L" => FakeProcessRunner.Streamed(ResolveCapture),
            "-G" => FakeProcessRunner.Streamed(AddressCapture),
            _ => FakeProcessRunner.Empty(),
        });

        List<EasyWorshipEndpoint> endpoints = new();
        await foreach (EasyWorshipEndpoint e in Discovery(runner).BrowseAsync(TimeSpan.FromSeconds(2)))
        {
            endpoints.Add(e);
        }

        await Assert.That(endpoints.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveOnceAsync_falls_back_to_local_host_when_G_yields_nothing()
    {
        FakeProcessRunner runner = Runner(verb => verb switch
        {
            "-B" => FakeProcessRunner.Streamed(BrowseCapture),
            "-L" => FakeProcessRunner.Streamed(ResolveCapture),
            _ => FakeProcessRunner.Empty(),
        });

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromSeconds(2));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("ew-host.local");
        await Assert.That(endpoint.Port).IsEqualTo(9888);
        await Assert.That(endpoint.Addresses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveOnceAsync_passes_unescaped_instance_name_to_L()
    {
        const string escapedBrowse =
            " 7:18:51.660 Add  3 56 local.    _ezwremote._tcp.    EasyWorship\\032Remote\\032(STUDIO)\n";

        FakeProcessRunner runner = Runner(verb => verb switch
        {
            "-B" => FakeProcessRunner.Streamed(escapedBrowse),
            "-L" => FakeProcessRunner.Streamed(ResolveCapture),
            "-G" => FakeProcessRunner.Streamed(AddressCapture),
            _ => FakeProcessRunner.Empty(),
        });

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromSeconds(2));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.InstanceName).IsEqualTo("EasyWorship Remote (STUDIO)");

        IReadOnlyList<string> resolveCall = runner.Invocations.Single(a => a.Count > 0 && a[0] == "-L");
        await Assert.That(resolveCall[1]).IsEqualTo("EasyWorship Remote (STUDIO)");
        await Assert.That(resolveCall[2]).IsEqualTo("_ezwremote._tcp");
        await Assert.That(resolveCall[3]).IsEqualTo("local");
    }

    [Test]
    public async Task ResolveOnceAsync_returns_null_on_empty_browse()
    {
        FakeProcessRunner runner = Runner(_ => FakeProcessRunner.Empty());

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromMilliseconds(50));

        await Assert.That(endpoint).IsNull();
    }

    [Test]
    public async Task ResolveOnceAsync_returns_null_on_garbage_browse()
    {
        FakeProcessRunner runner = Runner(verb => verb == "-B"
            ? new ProcessRunResult(ExitCode: 1, "totally unparseable\n", "boom", TimedOut: false)
            : FakeProcessRunner.Empty());

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromMilliseconds(50));

        await Assert.That(endpoint).IsNull();
    }

    [Test]
    public async Task ResolveOnceAsync_returns_null_when_resolve_fails()
    {
        FakeProcessRunner runner = Runner(verb => verb == "-B"
            ? FakeProcessRunner.Streamed(BrowseCapture)
            : FakeProcessRunner.Empty());

        EasyWorshipEndpoint? endpoint = await Discovery(runner).ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNull();
    }
}
