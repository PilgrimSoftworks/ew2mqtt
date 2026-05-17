using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Discovers EasyWorship via Apple Bonjour's <c>dns-sd</c> CLI. On Windows the
/// Bonjour service (<c>mDNSResponder.exe</c>) owns UDP 5353, so the managed
/// <see cref="ZeroconfEasyWorshipDiscovery"/> never receives answers; driving
/// <c>dns-sd</c> (a thin IPC client of that service) does. Also the right path
/// on macOS and Linux-with-avahi-compat.
/// </summary>
public sealed class BonjourEasyWorshipDiscovery : IEasyWorshipDiscovery
{
    // dns-sd uses the bare service type ("_ezwremote._tcp"), not the
    // fully-qualified ".local." form the managed resolver wants.
    private const string DnsSdServiceType = "_ezwremote._tcp";

    private static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(2);

    private readonly string _dnsSdPath;
    private readonly IProcessRunner _runner;
    private readonly ILogger<BonjourEasyWorshipDiscovery> _logger;

    public BonjourEasyWorshipDiscovery(string dnsSdPath, ILogger<BonjourEasyWorshipDiscovery>? logger = null)
        : this(dnsSdPath, new ProcessRunner(), logger)
    {
    }

    internal BonjourEasyWorshipDiscovery(
        string dnsSdPath,
        IProcessRunner runner,
        ILogger<BonjourEasyWorshipDiscovery>? logger)
    {
        _dnsSdPath = dnsSdPath;
        _runner = runner;
        _logger = logger ?? NullLogger<BonjourEasyWorshipDiscovery>.Instance;
    }

    public async IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + window;

        // -B streams forever; spend roughly half the window collecting Add
        // rows, leaving the rest for the per-instance -L/-G calls.
        TimeSpan browseBudget = Clamp(TimeSpan.FromTicks(window.Ticks / 2), MinStep, window);
        ProcessRunResult browse = await _runner
            .RunAsync(_dnsSdPath, new[] { "-B", DnsSdServiceType }, browseBudget, ct, DnsSdParser.IsBrowseAddLine)
            .ConfigureAwait(false);

        IReadOnlyList<string> instances = DnsSdParser.ParseBrowse(browse.StandardOutput);
        if (instances.Count == 0)
        {
            _logger.LogDebug("dns-sd -B returned no EasyWorship instances");
            yield break;
        }

        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            ProcessRunResult resolve = await _runner
                .RunAsync(
                    _dnsSdPath,
                    new[] { "-L", instance, DnsSdServiceType, "local" },
                    StepBudget(deadline),
                    ct,
                    DnsSdParser.IsReachableLine)
                .ConfigureAwait(false);

            (string Host, int Port)? hostPort = DnsSdParser.ParseResolve(resolve.StandardOutput);
            if (hostPort is null)
            {
                _logger.LogDebug("dns-sd -L found no host/port for instance {Instance}", instance);
                continue;
            }

            (string? host, int port) = hostPort.Value;

            // .NET's own .local. resolution may also be Bonjour-blocked, so
            // prefer the numeric IPv4 from -G for Host when we get one. A
            // multi-homed host answers -G with one row per interface; collect
            // them all (no early-stop) so a downstream reachability check has
            // every candidate, not just whichever interface replied first.
            ProcessRunResult address = await _runner
                .RunAsync(_dnsSdPath, new[] { "-G", "v4", host }, StepBudget(deadline), ct)
                .ConfigureAwait(false);

            IReadOnlyList<IPAddress> addresses = DnsSdParser.ParseAddresses(address.StandardOutput);
            string effectiveHost = addresses.Count > 0 ? addresses[0].ToString() : host;

            string key = $"{instance}|{effectiveHost}|{port}";
            if (!emitted.Add(key))
            {
                continue;
            }

            _logger.LogDebug(
                "Discovered EasyWorship via Bonjour: {Name} at {Host}:{Port}",
                instance, effectiveHost, port);
            yield return new EasyWorshipEndpoint(instance, effectiveHost, port, addresses);
        }
    }

    public async Task<EasyWorshipEndpoint?> ResolveOnceAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await foreach (EasyWorshipEndpoint? endpoint in BrowseAsync(timeout, ct).ConfigureAwait(false))
        {
            return endpoint;
        }
        return null;
    }

    private static TimeSpan StepBudget(DateTimeOffset deadline)
        => Clamp(deadline - DateTimeOffset.UtcNow, MinStep, MaxStep);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            max = min;
        }
        if (value < min)
        {
            return min;
        }
        return value > max ? max : value;
    }
}
