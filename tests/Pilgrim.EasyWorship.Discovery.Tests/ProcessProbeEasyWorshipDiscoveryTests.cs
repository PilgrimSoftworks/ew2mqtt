namespace Pilgrim.EasyWorship.Discovery.Tests;

public sealed class ProcessProbeEasyWorshipDiscoveryTests
{
    // Mirrors a real capture: a main EasyWorship.exe plus a helper; only the
    // main process listens, on a dynamic port (EW has no fixed port).
    private const string Tasklist =
        "\"EasyWorship.exe\",\"12345\",\"Console\",\"1\",\"350,000 K\"\r\n" +
        "\"EasyWorshipHelper.exe\",\"6789\",\"Console\",\"1\",\"12,000 K\"\r\n";

    private const string Netstat =
        "  TCP    0.0.0.0:55243          0.0.0.0:0              LISTENING       12345\r\n" +
        "  TCP    10.0.0.5:51000         52.1.2.3:443           ESTABLISHED     12345\r\n";

    private static FakeProcessRunner Runner(Func<string, ProcessRunResult> byArg0)
        => new(args => byArg0(args.Count > 0 ? args[0] : string.Empty));

    private static ProcessRunResult Out(string stdout)
        => new(ExitCode: 0, stdout, string.Empty, TimedOut: false);

    [Test]
    public async Task WindowsNative_returns_loopback_endpoint_on_dynamic_port()
    {
        FakeProcessRunner runner = Runner(arg0 => arg0 switch
        {
            "/FO" => Out(Tasklist),
            "-ano" => Out(Netstat),
            _ => Out(string.Empty),
        });

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.WindowsNative, () => "127.0.0.1", null);
        EasyWorshipEndpoint? endpoint = await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("127.0.0.1");
        await Assert.That(endpoint.Port).IsEqualTo(55243);
        await Assert.That(endpoint.InstanceName).IsEqualTo("EasyWorship (local process)");
    }

    [Test]
    public async Task Wsl_uses_resolved_windows_host()
    {
        FakeProcessRunner runner = Runner(arg0 => arg0 switch
        {
            "/FO" => Out(Tasklist),
            "-ano" => Out(Netstat),
            _ => Out(string.Empty),
        });

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.Wsl, () => "172.20.0.1", null);
        EasyWorshipEndpoint? endpoint = await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("172.20.0.1");
        await Assert.That(endpoint.Port).IsEqualTo(55243);
        await Assert.That(endpoint.Addresses.Count).IsEqualTo(1);
        await Assert.That(endpoint.Addresses[0].ToString()).IsEqualTo("172.20.0.1");

        // tasklist.exe / netstat.exe (interop) must be the binaries invoked.
        await Assert.That(runner.Invocations.Any(a => a.Contains("/FO"))).IsTrue();
        await Assert.That(runner.Invocations.Any(a => a.Contains("-ano"))).IsTrue();
    }

    [Test]
    public async Task Picks_the_process_that_actually_listens()
    {
        // Two EasyWorship processes; only the helper (6789) has a socket.
        const string netstatOnlyHelper =
            "  TCP    0.0.0.0:49876          0.0.0.0:0              LISTENING       6789\r\n" +
            "  TCP    10.0.0.5:51000         52.1.2.3:443           ESTABLISHED     12345\r\n";

        FakeProcessRunner runner = Runner(arg0 => arg0 switch
        {
            "/FO" => Out(Tasklist),
            "-ano" => Out(netstatOnlyHelper),
            _ => Out(string.Empty),
        });

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.WindowsNative, () => "127.0.0.1", null);
        EasyWorshipEndpoint? endpoint = await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Port).IsEqualTo(49876);
    }

    [Test]
    public async Task Returns_null_when_no_easyworship_process()
    {
        FakeProcessRunner runner = Runner(arg0 => arg0 == "/FO"
            ? Out("\"notepad.exe\",\"999\",\"Console\",\"1\",\"8,000 K\"\r\n")
            : Out(Netstat));

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.WindowsNative, () => "127.0.0.1", null);

        await Assert.That(await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1))).IsNull();
    }

    [Test]
    public async Task Returns_null_when_process_exists_but_not_listening()
    {
        FakeProcessRunner runner = Runner(arg0 => arg0 == "/FO" ? Out(Tasklist) : Out(string.Empty));

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.WindowsNative, () => "127.0.0.1", null);

        await Assert.That(await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1))).IsNull();
    }

    [Test]
    public async Task Linux_uses_ss_and_loopback()
    {
        const string ss =
            "LISTEN 0 128 0.0.0.0:48210 0.0.0.0:* users:((\"EasyWorship\",pid=4321,fd=7))\n";

        FakeProcessRunner runner = Runner(arg0 => arg0 == "-tlnp" ? Out(ss) : Out(string.Empty));

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.Linux, () => "127.0.0.1", null);
        EasyWorshipEndpoint? endpoint = await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1));

        await Assert.That(endpoint).IsNotNull();
        await Assert.That(endpoint!.Host).IsEqualTo("127.0.0.1");
        await Assert.That(endpoint.Port).IsEqualTo(48210);
    }

    [Test]
    public async Task Unsupported_platform_returns_null_without_running_anything()
    {
        FakeProcessRunner runner = Runner(_ => Out("should not be called"));

        ProcessProbeEasyWorshipDiscovery probe = new(
            runner, ProbePlatform.Unsupported, () => "127.0.0.1", null);

        await Assert.That(await probe.ResolveOnceAsync(TimeSpan.FromSeconds(1))).IsNull();
        await Assert.That(runner.Invocations.Count).IsEqualTo(0);
    }
}
