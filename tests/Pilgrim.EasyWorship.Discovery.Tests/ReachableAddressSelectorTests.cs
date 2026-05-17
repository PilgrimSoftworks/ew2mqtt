using System.Net;

namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class ReachableAddressSelectorTests
{
    private static EasyWorshipEndpoint Endpoint(
        string host, int port, params string[] addresses)
        => new(
            "EasyWorship Remote (STUDIO)",
            host,
            port,
            addresses.Select(IPAddress.Parse).ToArray());

    private static IReadOnlyList<string> Ranked(EasyWorshipEndpoint endpoint)
        => ReachableAddressSelector.RankCandidates(endpoint)
            .Select(ip => ip.ToString())
            .ToArray();

    // ── RankCandidates (pure) ──────────────────────────────────────────────

    [Test]
    public async Task RankCandidates_drops_linklocal_and_cgnat_keeps_private()
    {
        // The real STUDIO failure: Tailscale 100.x first, LAN 192.168 reachable.
        IReadOnlyList<string> ranked = Ranked(Endpoint(
            "100.64.0.1", 55243,
            "100.64.0.1", "192.168.10.20", "169.254.1.2", "fe80::1"));

        await Assert.That(ranked.Count).IsEqualTo(1);
        await Assert.That(ranked[0]).IsEqualTo("192.168.10.20");
    }

    [Test]
    public async Task RankCandidates_orders_private_over_public_over_ipv6_over_loopback()
    {
        IReadOnlyList<string> ranked = Ranked(Endpoint(
            "ew.local.", 9888,
            "127.0.0.1", "2001:db8::1", "8.8.8.8", "10.1.2.3",
            "fe80::1", "169.254.1.2", "100.64.0.1"));

        await Assert.That(ranked.Count).IsEqualTo(4);
        await Assert.That(ranked[0]).IsEqualTo("10.1.2.3");   // RFC1918
        await Assert.That(ranked[1]).IsEqualTo("8.8.8.8");    // ordinary IPv4
        await Assert.That(ranked[2]).IsEqualTo("2001:db8::1"); // routable IPv6
        await Assert.That(ranked[3]).IsEqualTo("127.0.0.1");  // loopback last
    }

    [Test]
    public async Task RankCandidates_includes_host_when_it_is_an_ip_and_addresses_empty()
    {
        IReadOnlyList<string> ranked = Ranked(Endpoint("192.168.1.9", 9888));

        await Assert.That(ranked.Count).IsEqualTo(1);
        await Assert.That(ranked[0]).IsEqualTo("192.168.1.9");
    }

    [Test]
    public async Task RankCandidates_empty_when_name_host_and_no_addresses()
    {
        IReadOnlyList<IPAddress> ranked = ReachableAddressSelector.RankCandidates(
            new EasyWorshipEndpoint("EW", "bree.local.", 9888, []),
            resolveHostName: _ => Array.Empty<IPAddress>());

        await Assert.That(ranked.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RankCandidates_expands_name_host_via_resolver_when_no_addresses()
    {
        string[] ranked = ReachableAddressSelector.RankCandidates(
            new EasyWorshipEndpoint("EW", "bree.local.", 9888, []),
            resolveHostName: name => name == "bree.local."
                ? [IPAddress.Parse("100.64.0.7"), IPAddress.Parse("192.168.5.5")]
                : Array.Empty<IPAddress>())
            .Select(ip => ip.ToString())
            .ToArray();

        await Assert.That(ranked.Count).IsEqualTo(1); // CGNAT dropped
        await Assert.That(ranked[0]).IsEqualTo("192.168.5.5");
    }

    [Test]
    public async Task RankCandidates_handles_empty_endpoint()
    {
        IReadOnlyList<IPAddress> ranked = ReachableAddressSelector.RankCandidates(
            new EasyWorshipEndpoint("EW", "", 9888, []),
            resolveHostName: _ => Array.Empty<IPAddress>());

        await Assert.That(ranked.Count).IsEqualTo(0);
    }

    // ── SelectAsync (probe seam) ───────────────────────────────────────────

    [Test]
    public async Task SelectAsync_picks_first_reachable_in_rank_order()
    {
        FakeTcpProbe probe = FakeTcpProbe.Reachable("10.0.0.5");
        ReachableAddressSelector selector = new(probe);

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("ew.local.", 9888, "192.168.10.20", "10.0.0.5"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("10.0.0.5");
        // 192.168.10.20 ranks equal-and-first; probed and refused before 10.0.0.5.
        await Assert.That(probe.Attempts[0].Address.ToString()).IsEqualTo("192.168.10.20");
        await Assert.That(probe.Attempts.Count).IsEqualTo(2);
        await Assert.That(result.Addresses.Count).IsEqualTo(2); // list preserved
    }

    [Test]
    public async Task SelectAsync_returns_original_unchanged_when_nothing_reachable()
    {
        EasyWorshipEndpoint original = Endpoint("100.64.0.1", 55243, "100.64.0.1", "192.168.10.20");
        ReachableAddressSelector selector = new(FakeTcpProbe.None());

        EasyWorshipEndpoint result = await selector.SelectAsync(
            original, TimeSpan.FromSeconds(1), CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("100.64.0.1");
        await Assert.That(result.Addresses.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SelectAsync_keeps_original_host_when_already_reachable()
    {
        FakeTcpProbe probe = FakeTcpProbe.Reachable("192.168.1.50");
        ReachableAddressSelector selector = new(probe);

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("192.168.1.50", 9888, "192.168.1.50"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("192.168.1.50");
        await Assert.That(probe.Attempts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SelectAsync_recovers_the_real_bree_failure()
    {
        // Discovery handed back the Tailscale address; only the LAN one listens.
        EasyWorshipEndpoint original = Endpoint(
            "100.64.0.1", 55243, "100.64.0.1", "192.168.10.20");
        ReachableAddressSelector selector = new(FakeTcpProbe.Reachable("192.168.10.20"));

        EasyWorshipEndpoint result = await selector.SelectAsync(
            original, TimeSpan.FromSeconds(2), CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("192.168.10.20");
        await Assert.That(result.Port).IsEqualTo(55243);
    }

    [Test]
    public async Task SelectAsync_does_not_throw_when_probe_throws()
    {
        ReachableAddressSelector selector = new(
            new FakeTcpProbe((_, _) => throw new InvalidOperationException("boom")));

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("ew.local.", 9888, "192.168.1.50"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("ew.local.");
    }

    [Test]
    public async Task SelectAsync_skips_cgnat_advertised_but_still_probes_loopback_fallback()
    {
        // Only a CGNAT advertised address: excluded outright (never probed),
        // but the synthesized loopback fallback is still tried. Nothing
        // answers here, so the original endpoint is returned unchanged.
        FakeTcpProbe probe = FakeTcpProbe.None();
        ReachableAddressSelector selector = new(
            probe, wslHostCandidates: () => Array.Empty<IPAddress>());

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("100.64.0.1", 55243, "100.64.0.1"),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("100.64.0.1");
        await Assert.That(probe.Attempts.Count).IsEqualTo(1);
        await Assert.That(probe.Attempts[0].Address.ToString()).IsEqualTo("127.0.0.1");
    }

    // ── Synthesized loopback / WSL-host fallback ───────────────────────────

    [Test]
    public async Task SelectAsync_uses_loopback_fallback_when_no_advertised_reachable()
    {
        // WSL2 mirrored networking: EW on the Windows host is reachable from
        // the VM only via 127.0.0.1, which is never advertised over Bonjour.
        FakeTcpProbe probe = FakeTcpProbe.Reachable("127.0.0.1");
        ReachableAddressSelector selector = new(
            probe, wslHostCandidates: () => Array.Empty<IPAddress>());

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("100.64.0.1", 55243, "172.22.112.1", "192.168.10.178"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("127.0.0.1");
        await Assert.That(result.Port).IsEqualTo(55243);
        // Advertised probed first, then the synthesized loopback fallback.
        await Assert.That(probe.Attempts[0].Address.ToString()).IsEqualTo("172.22.112.1");
        await Assert.That(probe.Attempts[1].Address.ToString()).IsEqualTo("192.168.10.178");
        await Assert.That(probe.Attempts[2].Address.ToString()).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task SelectAsync_never_probes_fallbacks_when_advertised_reachable()
    {
        // A reachable advertised RFC1918 address must win outright — LAN
        // deployments (phone on Wi-Fi) are unaffected and the WSL candidate
        // source is not even consulted.
        FakeTcpProbe probe = FakeTcpProbe.Reachable("192.168.10.178");
        bool wslConsulted = false;
        ReachableAddressSelector selector = new(
            probe,
            wslHostCandidates: () =>
            {
                wslConsulted = true;
                return Array.Empty<IPAddress>();
            });

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("100.64.0.1", 55243, "172.22.112.1", "192.168.10.178"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("192.168.10.178");
        await Assert.That(wslConsulted).IsFalse();
        await Assert.That(
                probe.Attempts.Any(a => a.Address.ToString() == "127.0.0.1"))
            .IsFalse();
        await Assert.That(probe.Attempts.Count).IsEqualTo(2); // advertised only
    }

    [Test]
    public async Task SelectAsync_uses_wsl_gateway_when_advertised_and_loopback_refuse()
    {
        // Under WSL2 NAT networking the Windows host answers on the WSL
        // default gateway (resolv.conf nameserver is the next candidate).
        FakeTcpProbe probe = FakeTcpProbe.Reachable("172.30.96.1");
        ReachableAddressSelector selector = new(
            probe,
            wslHostCandidates: () =>
                [IPAddress.Parse("172.30.96.1"), IPAddress.Parse("172.30.96.2")]);

        EasyWorshipEndpoint result = await selector.SelectAsync(
            Endpoint("100.64.0.1", 55243, "192.168.10.178"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("172.30.96.1");
        await Assert.That(result.Port).IsEqualTo(55243);
        // Order: advertised → loopback → WSL gateway (nameserver never reached).
        await Assert.That(probe.Attempts.Count).IsEqualTo(3);
        await Assert.That(probe.Attempts[0].Address.ToString()).IsEqualTo("192.168.10.178");
        await Assert.That(probe.Attempts[1].Address.ToString()).IsEqualTo("127.0.0.1");
        await Assert.That(probe.Attempts[2].Address.ToString()).IsEqualTo("172.30.96.1");
    }

    [Test]
    public async Task SelectAsync_returns_original_when_nothing_reachable_anywhere()
    {
        EasyWorshipEndpoint original = Endpoint("100.64.0.1", 55243, "192.168.10.178");
        ReachableAddressSelector selector = new(
            FakeTcpProbe.None(),
            wslHostCandidates: () => [IPAddress.Parse("172.30.96.1")]);

        EasyWorshipEndpoint result = await selector.SelectAsync(
            original, TimeSpan.FromSeconds(2), CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("100.64.0.1");
        await Assert.That(result.Port).IsEqualTo(55243);
        await Assert.That(result.Addresses.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SelectAsync_recovers_the_real_mirrored_wsl_failure()
    {
        // The captured STUDIO failure under WSL2 mirrored networking: advertised
        // [172.22.112.1, 192.168.10.178], discovery returned the Tailscale
        // CGNAT host (excluded), port 55243, only 127.0.0.1:55243 listening.
        EasyWorshipEndpoint original = Endpoint(
            "100.64.0.1", 55243, "172.22.112.1", "192.168.10.178");
        ReachableAddressSelector selector = new(
            FakeTcpProbe.Reachable("127.0.0.1"),
            wslHostCandidates: () => Array.Empty<IPAddress>());

        EasyWorshipEndpoint result = await selector.SelectAsync(
            original, TimeSpan.FromSeconds(2), CancellationToken.None);

        await Assert.That(result.Host).IsEqualTo("127.0.0.1");
        await Assert.That(result.Port).IsEqualTo(55243);
        await Assert.That(result.Addresses.Count).IsEqualTo(2); // intact
        await Assert.That(result.InstanceName).IsEqualTo("EasyWorship Remote (STUDIO)");
    }
}
