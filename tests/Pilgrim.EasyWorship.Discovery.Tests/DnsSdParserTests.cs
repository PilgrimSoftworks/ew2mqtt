using System.Net;

namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class DnsSdParserTests
{
    // Real capture: instance name with spaces + parens, duplicated per interface.
    private const string BrowseCapture =
        "Browsing for _ezwremote._tcp\n" +
        "Timestamp  A/R Flags if Domain   Service Type        Instance Name\n" +
        " 7:18:51.660 Add  3 56 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n" +
        " 7:18:51.660 Add  3 41 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n" +
        " 7:18:51.660 Add  2 23 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n";

    [Test]
    public async Task ParseBrowse_dedupes_instance_across_interfaces()
    {
        IReadOnlyList<string> names = DnsSdParser.ParseBrowse(BrowseCapture);

        await Assert.That(names.Count).IsEqualTo(1);
        await Assert.That(names[0]).IsEqualTo("EasyWorship Remote (STUDIO)");
    }

    [Test]
    public async Task ParseBrowse_ignores_remove_rows_and_headers()
    {
        const string capture =
            "Browsing for _ezwremote._tcp\n" +
            " 7:18:51.660 Add  3 56 local.    _ezwremote._tcp.    EasyWorship Remote (STUDIO)\n" +
            " 7:18:55.001 Rmv  3 56 local.    _ezwremote._tcp.    Stale Instance\n";

        IReadOnlyList<string> names = DnsSdParser.ParseBrowse(capture);

        await Assert.That(names.Count).IsEqualTo(1);
        await Assert.That(names[0]).IsEqualTo("EasyWorship Remote (STUDIO)");
    }

    [Test]
    public async Task ParseBrowse_decodes_bonjour_escapes()
    {
        const string capture =
            " 7:18:51.660 Add  3 56 local.    _ezwremote._tcp.    EasyWorship\\032Remote\\032(STUDIO)\n";

        IReadOnlyList<string> names = DnsSdParser.ParseBrowse(capture);

        await Assert.That(names.Count).IsEqualTo(1);
        await Assert.That(names[0]).IsEqualTo("EasyWorship Remote (STUDIO)");
    }

    [Test]
    public async Task Unescape_handles_decimal_and_literal_escapes()
    {
        await Assert.That(DnsSdParser.Unescape(@"a\032b")).IsEqualTo("a b");
        await Assert.That(DnsSdParser.Unescape(@"x\.y")).IsEqualTo("x.y");
        await Assert.That(DnsSdParser.Unescape(@"c\\d")).IsEqualTo(@"c\d");
        await Assert.That(DnsSdParser.Unescape("plain")).IsEqualTo("plain");
    }

    [Test]
    public async Task ParseResolve_extracts_host_and_port()
    {
        const string capture =
            "Lookup EasyWorship Remote (STUDIO)._ezwremote._tcp.local\n" +
            "DATE: ---Sat 17 May 2026---\n" +
            " 7:20:01.456  EasyWorship\\032Remote\\032(STUDIO)._ezwremote._tcp.local. " +
            "can be reached at ew-host.local.:9888 (interface 56)\n" +
            " 7:20:01.789  EasyWorship\\032Remote\\032(STUDIO)._ezwremote._tcp.local. " +
            "can be reached at ew-host.local.:9888 (interface 41)\n";

        (string Host, int Port)? result = DnsSdParser.ParseResolve(capture);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Host).IsEqualTo("ew-host.local");
        await Assert.That(result.Value.Port).IsEqualTo(9888);
    }

    [Test]
    public async Task ParseResolve_returns_null_on_garbage()
    {
        await Assert.That(DnsSdParser.ParseResolve("nothing useful here\n")).IsNull();
        await Assert.That(DnsSdParser.ParseResolve("")).IsNull();
    }

    [Test]
    public async Task ParseAddress_extracts_first_ipv4_from_add_row()
    {
        const string capture =
            "Timestamp  A/R Flags if Hostname        Address       TTL\n" +
            " 7:20:10.111 Add  2 56 ew-host.local.  192.168.1.50  120\n";

        IPAddress? ip = DnsSdParser.ParseAddress(capture);

        await Assert.That(ip).IsNotNull();
        await Assert.That(ip!.ToString()).IsEqualTo("192.168.1.50");
    }

    [Test]
    public async Task ParseAddress_returns_null_when_no_add_row()
    {
        const string capture =
            " 7:20:10.111 Rmv  2 56 ew-host.local.  192.168.1.50  120\n";

        await Assert.That(DnsSdParser.ParseAddress(capture)).IsNull();
        await Assert.That(DnsSdParser.ParseAddress("garbage\n")).IsNull();
        await Assert.That(DnsSdParser.ParseAddress("")).IsNull();
    }

    [Test]
    public async Task ParseAddresses_returns_all_interfaces_deduped_in_order()
    {
        // Multi-homed STUDIO: Tailscale, then LAN (twice across interfaces).
        const string capture =
            "Timestamp  A/R Flags if Hostname        Address          TTL\n" +
            " 7:20:10.111 Add  2 56 ew-host.local.  100.64.0.1   120\n" +
            " 7:20:10.222 Add  2 41 ew-host.local.  192.168.10.20    120\n" +
            " 7:20:10.333 Add  2 23 ew-host.local.  192.168.10.20    120\n";

        IReadOnlyList<IPAddress> ips = DnsSdParser.ParseAddresses(capture);

        await Assert.That(ips.Count).IsEqualTo(2);
        await Assert.That(ips[0].ToString()).IsEqualTo("100.64.0.1");
        await Assert.That(ips[1].ToString()).IsEqualTo("192.168.10.20");
    }

    [Test]
    public async Task ParseAddresses_empty_on_no_add_rows_or_garbage()
    {
        await Assert.That(DnsSdParser.ParseAddresses(
            " 7:20:10.111 Rmv  2 56 ew-host.local.  192.168.1.50  120\n").Count).IsEqualTo(0);
        await Assert.That(DnsSdParser.ParseAddresses("garbage\n").Count).IsEqualTo(0);
        await Assert.That(DnsSdParser.ParseAddresses("").Count).IsEqualTo(0);
    }
}
