
namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class ProcessProbeParserTests
{
    private const string Tasklist =
        "\"EasyWorship.exe\",\"12345\",\"Console\",\"1\",\"350,000 K\"\r\n" +
        "\"EasyWorship Updater.exe\",\"6789\",\"Console\",\"1\",\"12,000 K\"\r\n" +
        "\"notepad.exe\",\"999\",\"Console\",\"1\",\"8,000 K\"\r\n";

    private const string Netstat =
        "\r\nActive Connections\r\n\r\n" +
        "  Proto  Local Address          Foreign Address        State           PID\r\n" +
        "  TCP    0.0.0.0:9888           0.0.0.0:0              LISTENING       12345\r\n" +
        "  TCP    0.0.0.0:52345          0.0.0.0:0              LISTENING       6789\r\n" +
        "  TCP    0.0.0.0:445            0.0.0.0:0              LISTENING       4\r\n" +
        "  TCP    10.0.0.5:51000         52.1.2.3:443           ESTABLISHED     12345\r\n" +
        "  TCP    [::]:9888              [::]:0                 LISTENING       12345\r\n";

    [Test]
    public async Task ParseEasyWorshipPids_matches_all_easyworship_images()
    {
        IReadOnlySet<int> pids = ProcessProbeParser.ParseEasyWorshipPids(Tasklist);

        await Assert.That(pids.Count).IsEqualTo(2);
        await Assert.That(pids.Contains(12345)).IsTrue();
        await Assert.That(pids.Contains(6789)).IsTrue();
        await Assert.That(pids.Contains(999)).IsFalse();
    }

    [Test]
    public async Task ParseNetstatListeners_extracts_listening_rows_only()
    {
        IReadOnlyList<(int Port, int Pid)> listeners = ProcessProbeParser.ParseNetstatListeners(Netstat);

        await Assert.That(listeners.Count).IsEqualTo(4);
        await Assert.That(listeners.Any(l => l is { Port: 9888, Pid: 12345 })).IsTrue();
        await Assert.That(listeners.Any(l => l.Port == 51000)).IsFalse();
    }

    [Test]
    public async Task ChoosePort_takes_the_single_listening_port()
    {
        // EasyWorship's ezwremote port is dynamic; there is no well-known
        // port to prefer — take whatever the process is actually listening on.
        int? port = ProcessProbeParser.ChoosePort([55243], out bool ambiguous);
        await Assert.That(port).IsEqualTo(55243);
        await Assert.That(ambiguous).IsFalse();
    }

    [Test]
    public async Task ChoosePort_lowest_and_flags_ambiguous_when_several()
    {
        int? port = ProcessProbeParser.ChoosePort([52345, 40000], out bool ambiguous);
        await Assert.That(port).IsEqualTo(40000);
        await Assert.That(ambiguous).IsTrue();
    }

    [Test]
    public async Task ChoosePort_returns_null_when_no_candidates()
    {
        int? port = ProcessProbeParser.ChoosePort([], out bool ambiguous);
        await Assert.That(port).IsNull();
        await Assert.That(ambiguous).IsFalse();
    }

    [Test]
    public async Task ParseSsEasyWorshipPorts_matches_owning_process_name()
    {
        const string ss =
            "State  Recv-Q Send-Q Local Address:Port Peer Address:Port Process\n" +
            "LISTEN 0      128    0.0.0.0:9888       0.0.0.0:*         users:((\"EasyWorship\",pid=4321,fd=7))\n" +
            "LISTEN 0      128    127.0.0.1:631      0.0.0.0:*         users:((\"cupsd\",pid=1,fd=9))\n" +
            "LISTEN 0      128    [::]:8080          [::]:*            users:((\"someapp\",pid=2,fd=3))\n";

        IReadOnlyList<int> ports = ProcessProbeParser.ParseSsEasyWorshipPorts(ss);

        await Assert.That(ports.Count).IsEqualTo(1);
        await Assert.That(ports[0]).IsEqualTo(9888);
    }

    [Test]
    public async Task ParseNetstatListeners_handles_ipv6_and_loopback_forms()
    {
        // The local-address token is greedy up to the final ':port'; lock that
        // it lands on the port for bracketed/scoped IPv6 and IPv4 loopback.
        const string netstat =
            "  TCP    [::]:9888              [::]:0                 LISTENING       111\r\n" +
            "  TCP    [::1]:9889             [::]:0                 LISTENING       222\r\n" +
            "  TCP    [fe80::1%eth0]:9890    [::]:0                 LISTENING       333\r\n" +
            "  TCP    127.0.0.1:9891         0.0.0.0:0              LISTENING       444\r\n";

        IReadOnlyList<(int Port, int Pid)> listeners = ProcessProbeParser.ParseNetstatListeners(netstat);

        await Assert.That(listeners.Count).IsEqualTo(4);
        await Assert.That(listeners.Any(l => l is { Port: 9888, Pid: 111 })).IsTrue();
        await Assert.That(listeners.Any(l => l is { Port: 9889, Pid: 222 })).IsTrue();
        await Assert.That(listeners.Any(l => l is { Port: 9890, Pid: 333 })).IsTrue();
        await Assert.That(listeners.Any(l => l is { Port: 9891, Pid: 444 })).IsTrue();
    }

    [Test]
    public async Task ParseSsEasyWorshipPorts_handles_wildcard_and_ipv6_local_address()
    {
        const string ss =
            "State  Recv-Q Send-Q Local Address:Port Peer Address:Port Process\n" +
            "LISTEN 0      128    *:9888             *:*               users:((\"EasyWorship\",pid=1,fd=7))\n" +
            "LISTEN 0      128    [::]:9889          [::]:*            users:((\"EasyWorship\",pid=2,fd=8))\n";

        IReadOnlyList<int> ports = ProcessProbeParser.ParseSsEasyWorshipPorts(ss);

        await Assert.That(ports.Count).IsEqualTo(2);
        await Assert.That(ports.Contains(9888)).IsTrue();
        await Assert.That(ports.Contains(9889)).IsTrue();
    }

    [Test]
    public async Task ParseEasyWorshipPids_tolerates_unquoted_fields()
    {
        // tasklist quotes every field, but an unquoted column must not truncate
        // the row before the PID is read.
        const string csv = "\"EasyWorship.exe\",54321,Console,1,\"350,000 K\"\r\n";

        IReadOnlySet<int> pids = ProcessProbeParser.ParseEasyWorshipPids(csv);

        await Assert.That(pids.Count).IsEqualTo(1);
        await Assert.That(pids.Contains(54321)).IsTrue();
    }

    [Test]
    public async Task Parsers_tolerate_empty_and_garbage()
    {
        await Assert.That(ProcessProbeParser.ParseEasyWorshipPids("").Count).IsEqualTo(0);
        await Assert.That(ProcessProbeParser.ParseNetstatListeners("junk\n").Count).IsEqualTo(0);
        await Assert.That(ProcessProbeParser.ParseSsEasyWorshipPorts("").Count).IsEqualTo(0);
    }
}
