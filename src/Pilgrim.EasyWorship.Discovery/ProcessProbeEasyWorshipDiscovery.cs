using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Last-resort, same-machine discovery: find the EasyWorship process that is
/// actually <c>LISTEN</c>ing on a TCP port and point at it directly. Handy when
/// mDNS yields nothing (Bonjour stopped, multicast filtered). Under WSL2
/// "same machine" is the Windows host, so it shells out via interop
/// (<c>tasklist.exe</c>/<c>netstat.exe</c>) and connects on the Windows host IP.
/// </summary>
public sealed class ProcessProbeEasyWorshipDiscovery : IEasyWorshipDiscovery
{
    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(4);

    private readonly IProcessRunner _runner;
    private readonly ProbePlatform _platform;
    private readonly Func<string> _windowsHostResolver;
    private readonly ILogger<ProcessProbeEasyWorshipDiscovery> _logger;

    public ProcessProbeEasyWorshipDiscovery(ILogger<ProcessProbeEasyWorshipDiscovery>? logger = null)
        : this(new ProcessRunner(), DetectPlatform(), WslEnvironment.ResolveWindowsHost, logger)
    {
    }

    internal ProcessProbeEasyWorshipDiscovery(
        IProcessRunner runner,
        ProbePlatform platform,
        Func<string> windowsHostResolver,
        ILogger<ProcessProbeEasyWorshipDiscovery>? logger)
    {
        _runner = runner;
        _platform = platform;
        _windowsHostResolver = windowsHostResolver;
        _logger = logger ?? NullLogger<ProcessProbeEasyWorshipDiscovery>.Instance;
    }

    public async IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EasyWorshipEndpoint? endpoint = await ProbeAsync(window, ct).ConfigureAwait(false);
        if (endpoint is not null)
        {
            yield return endpoint;
        }
    }

    public Task<EasyWorshipEndpoint?> ResolveOnceAsync(TimeSpan timeout, CancellationToken ct = default)
        => ProbeAsync(timeout, ct);

    private async Task<EasyWorshipEndpoint?> ProbeAsync(TimeSpan budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TimeSpan step = budget < CommandBudget && budget > TimeSpan.Zero ? budget : CommandBudget;

        (int? port, string? host) = _platform switch
        {
            ProbePlatform.WindowsNative => (await WindowsPortAsync(step, ct).ConfigureAwait(false), "127.0.0.1"),
            ProbePlatform.Wsl => (await WindowsPortAsync(step, ct).ConfigureAwait(false), ResolveWindowsHostSafe()),
            ProbePlatform.Linux => (await LinuxPortAsync(step, ct).ConfigureAwait(false), "127.0.0.1"),
            _ => ((int?)null, "127.0.0.1"),
        };

        if (port is null)
        {
            _logger.LogDebug("Process probe found no listening EasyWorship process");
            return null;
        }

        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out IPAddress? ip)
            ? (IReadOnlyList<IPAddress>)[ip]
            : Array.Empty<IPAddress>();

        _logger.LogDebug("Process probe located EasyWorship at {Host}:{Port}", host, port.Value);
        return new EasyWorshipEndpoint("EasyWorship (local process)", host, port.Value, addresses);
    }

    private async Task<int?> WindowsPortAsync(TimeSpan step, CancellationToken ct)
    {
        string tasklistExe = _platform == ProbePlatform.Wsl ? "tasklist.exe" : "tasklist";
        string netstatExe = _platform == ProbePlatform.Wsl ? "netstat.exe" : "netstat";

        ProcessRunResult tasklist = await _runner
            .RunAsync(tasklistExe, ["/FO", "CSV", "/NH"], step, ct)
            .ConfigureAwait(false);
        LogIfEmpty(tasklistExe, tasklist);
        IReadOnlySet<int> pids = ProcessProbeParser.ParseEasyWorshipPids(tasklist.StandardOutput);
        if (pids.Count == 0)
        {
            return null;
        }

        ProcessRunResult netstat = await _runner
            .RunAsync(netstatExe, ["-ano", "-p", "TCP"], step, ct)
            .ConfigureAwait(false);
        LogIfEmpty(netstatExe, netstat);

        IEnumerable<int> ports = ProcessProbeParser.ParseNetstatListeners(netstat.StandardOutput)
            .Where(x => pids.Contains(x.Pid))
            .Select(static x => x.Port);

        return Choose(ports);
    }

    private async Task<int?> LinuxPortAsync(TimeSpan step, CancellationToken ct)
    {
        // ss embeds the owning process name, so one call is enough.
        ProcessRunResult ss = await _runner.RunAsync("ss", ["-tlnp"], step, ct).ConfigureAwait(false);
        LogIfEmpty("ss", ss);
        return Choose(ProcessProbeParser.ParseSsEasyWorshipPorts(ss.StandardOutput));
    }

    // When a probe tool produces no stdout the cause (missing under WSL, denied,
    // timed out) is otherwise invisible — surface exit/stderr at Debug so
    // "discovery found nothing" is diagnosable in the field.
    private void LogIfEmpty(string tool, ProcessRunResult r)
    {
        if (!string.IsNullOrWhiteSpace(r.StandardOutput) || !_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }
        string stderr = r.StandardError.Trim();
        _logger.LogDebug(
            "{Tool} produced no output (exit={Exit}, timedOut={TimedOut}){Stderr}",
            tool,
            r.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
            r.TimedOut,
            stderr.Length == 0 ? "" : $": {stderr}");
    }

    private int? Choose(IEnumerable<int> ports)
    {
        int? port = ProcessProbeParser.ChoosePort(ports, out bool ambiguous);
        if (ambiguous)
        {
            _logger.LogDebug(
                "EasyWorship process has several listening ports; using the lowest ({Port})", port);
        }
        return port;
    }

    private string ResolveWindowsHostSafe()
    {
        try
        {
            string host = _windowsHostResolver();
            return string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        }
#pragma warning disable CA1031 // host resolution must never break discovery
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "WSL Windows-host resolution failed; using loopback");
            return "127.0.0.1";
        }
    }

    private static ProbePlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ProbePlatform.WindowsNative;
        }
        if (WslEnvironment.IsWsl)
        {
            return ProbePlatform.Wsl;
        }
        return OperatingSystem.IsLinux() ? ProbePlatform.Linux : ProbePlatform.Unsupported;
    }
}

internal enum ProbePlatform
{
    WindowsNative,
    Wsl,
    Linux,
    Unsupported,
}
