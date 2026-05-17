using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Turns a discovered <see cref="EasyWorshipEndpoint"/> into one whose
/// <c>Host</c> is an address the EasyWorship socket actually accepts.
/// <para>
/// Multi-homed Windows hosts (LAN + Tailscale/CGNAT + Hyper-V/WSL vEthernet)
/// advertise several A records over Bonjour; EasyWorship's ezwremote socket
/// only binds some of them. Discovery historically handed back whichever
/// address came first — frequently the Tailscale <c>100.64.0.0/10</c> one,
/// which refuses the connection. This ranks the candidates and connect-probes
/// them in order, committing to the first that answers.
/// </para>
/// <para>
/// It never regresses to "no endpoint": if every candidate fails (or there is
/// nothing better to try) the original endpoint is returned unchanged, so a
/// working LAN-only or process-probe case is preserved.
/// </para>
/// </summary>
internal sealed class ReachableAddressSelector
{
    /// <summary>Per-address connect budget; a refused peer answers far faster.</summary>
    internal static readonly TimeSpan DefaultPerAddressTimeout = TimeSpan.FromMilliseconds(700);

    private readonly ITcpProbe _probe;
    private readonly TimeSpan _perAddressTimeout;
    private readonly Func<string, IReadOnlyList<IPAddress>> _resolveHostName;
    private readonly Func<IReadOnlyList<IPAddress>> _wslHostCandidates;
    private readonly ILogger _logger;

    public ReachableAddressSelector(
        ITcpProbe probe,
        ILogger? logger = null,
        TimeSpan? perAddressTimeout = null,
        Func<string, IReadOnlyList<IPAddress>>? resolveHostName = null,
        Func<IReadOnlyList<IPAddress>>? wslHostCandidates = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? NullLogger.Instance;
        _perAddressTimeout = perAddressTimeout is { } t && t > TimeSpan.Zero
            ? t
            : DefaultPerAddressTimeout;
        _resolveHostName = resolveHostName ?? DefaultResolveHostName;
        _wslHostCandidates = wslHostCandidates ?? WslEnvironment.WindowsHostCandidates;
    }

    /// <summary>
    /// Probes the ranked advertised candidates within <paramref name="budget"/>
    /// and returns the endpoint pointed at the first reachable one. If none
    /// connect, a bounded set of synthesized fallbacks (loopback, then — under
    /// WSL — the WSL2 NAT-side Windows host) is probed within the same budget
    /// and the first that answers is adopted. If still nothing connects (or
    /// there is nothing to try) the original endpoint is returned unchanged.
    /// </summary>
    public async Task<EasyWorshipEndpoint> SelectAsync(
        EasyWorshipEndpoint endpoint, TimeSpan budget, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + budget;
        List<IPAddress> tried = new();

        // 1. Advertised addresses, ranked best-first. Tried first so real LAN
        //    deployments still pick the LAN address phones can reach.
        IReadOnlyList<IPAddress> ranked = RankCandidates(endpoint, _resolveHostName);
        IPAddress? reachable = await FirstReachableAsync(ranked, endpoint.Port, deadline, tried, ct)
            .ConfigureAwait(false);
        if (reachable is not null)
        {
            if (string.Equals(endpoint.Host, reachable.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Discovered EasyWorship host {Host}:{Port} is reachable",
                    endpoint.Host, endpoint.Port);
                return endpoint;
            }

            _logger.LogInformation(
                "Selected reachable EasyWorship address {Address}:{Port} (discovery returned {Host})",
                reachable, endpoint.Port, endpoint.Host);
            return endpoint with { Host = reachable.ToString() };
        }

        // 2. Nothing advertised answered. Under WSL2 "mirrored" networking EW
        //    does NOT advertise loopback, yet that (or the WSL2 NAT host) is
        //    the only address the VM can reach a Windows-host listener on.
        //    Connect-probe a bounded synthesized set via the same seam.
        IReadOnlyList<IPAddress> fallbacks = SynthesizedFallbacks(tried);
        reachable = await FirstReachableAsync(fallbacks, endpoint.Port, deadline, tried, ct)
            .ConfigureAwait(false);
        if (reachable is not null)
        {
            _logger.LogInformation(
                "No advertised EasyWorship address reachable; using fallback {Address}:{Port} (discovery returned {Host})",
                reachable, endpoint.Port, endpoint.Host);
            return endpoint with { Host = reachable.ToString() };
        }

        // 3. Nothing anywhere — never regress to "no endpoint".
        _logger.LogInformation(
            "No reachable EasyWorship address among [{Tried}] on port {Port}; keeping discovered host {Host}",
            string.Join(", ", tried), endpoint.Port, endpoint.Host);
        return endpoint;
    }

    /// <summary>
    /// Connect-probes <paramref name="candidates"/> in order within the shared
    /// <paramref name="deadline"/>, recording each attempt in
    /// <paramref name="tried"/>, and returns the first that accepts — or
    /// <c>null</c> if none do (or the budget ran out). A misbehaving probe is
    /// treated as unreachable; only genuine caller cancellation propagates.
    /// </summary>
    private async Task<IPAddress?> FirstReachableAsync(
        IReadOnlyList<IPAddress> candidates,
        int port,
        DateTimeOffset deadline,
        List<IPAddress> tried,
        CancellationToken ct)
    {
        foreach (IPAddress addr in candidates)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan perAddress = remaining < _perAddressTimeout ? remaining : _perAddressTimeout;
            tried.Add(addr);

            bool reachable;
            try
            {
                reachable = await _probe
                    .CanConnectAsync(addr, port, perAddress, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a misbehaving probe must not break selection
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(
                    ex, "TCP probe of {Address}:{Port} threw; treating as unreachable",
                    addr, port);
                reachable = false;
            }

            if (reachable)
            {
                return addr;
            }
        }

        return null;
    }

    /// <summary>
    /// The bounded synthesized fallback set, in probe order: loopback first
    /// (WSL "mirrored" networking, or EW running on the same box), then —
    /// under WSL — the WSL2 NAT-side Windows-host candidates (default gateway,
    /// <c>resolv.conf</c> nameserver). Addresses already probed in the
    /// advertised pass, and any excluded family (link-local / CGNAT), are
    /// skipped; de-duped. At most ~3 entries.
    /// </summary>
    private IReadOnlyList<IPAddress> SynthesizedFallbacks(IReadOnlyCollection<IPAddress> alreadyTried)
    {
        HashSet<IPAddress> seen = new(alreadyTried);
        List<IPAddress> result = new(3);

        void Add(IPAddress ip)
        {
            // Rank() == null screens out link-local / CGNAT (e.g. a WSL host
            // that is itself on a CGNAT range EW would never bind).
            if (seen.Add(ip) && Rank(ip) is not null)
            {
                result.Add(ip);
            }
        }

        Add(IPAddress.Loopback); // 127.0.0.1 — last resort; harmless off-WSL
        foreach (IPAddress ip in _wslHostCandidates())
        {
            Add(ip);
        }
        return result;
    }

    /// <summary>
    /// Pure: builds the candidate set (<c>Addresses ∪ {Host if it parses as an
    /// IP}</c>; or, for a bare name with no addresses, a best-effort DNS
    /// expansion) and returns it ranked best-first. Unreachable-by-construction
    /// families — IPv4/IPv6 link-local and CGNAT (<c>100.64.0.0/10</c>) — are
    /// dropped outright; loopback is kept only as a last resort.
    /// </summary>
    public static IReadOnlyList<IPAddress> RankCandidates(
        EasyWorshipEndpoint endpoint,
        Func<string, IReadOnlyList<IPAddress>>? resolveHostName = null)
    {
        HashSet<IPAddress> seen = new();
        List<IPAddress> candidates = new();

        void Add(IPAddress? ip)
        {
            if (ip is not null && seen.Add(ip))
            {
                candidates.Add(ip);
            }
        }

        foreach (IPAddress a in endpoint.Addresses)
        {
            Add(a);
        }

        if (IPAddress.TryParse(endpoint.Host, out IPAddress? hostIp))
        {
            Add(hostIp);
        }
        else if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(endpoint.Host))
        {
            foreach (IPAddress ip in (resolveHostName ?? DefaultResolveHostName)(endpoint.Host))
            {
                Add(ip);
            }
        }

        // OrderByDescending is a stable sort in LINQ-to-objects, so equal-rank
        // addresses keep their discovered order (Addresses before Host).
        return candidates
            .Select(static ip => (ip, rank: Rank(ip)))
            .Where(static x => x.rank is not null)
            .OrderByDescending(static x => x.rank!.Value)
            .Select(static x => x.ip)
            .ToArray();
    }

    /// <summary>
    /// Higher is better; <c>null</c> means "never usable, exclude". Tiers:
    /// RFC1918 private IPv4 (40) &gt; other ordinary IPv4 (30) &gt; routable
    /// IPv6 (20) &gt; loopback (1). Excluded: 169.254/16, fe80::/10, 100.64/10.
    /// </summary>
    private static int? Rank(IPAddress address)
    {
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                {
                    byte[] b = address.GetAddressBytes();

                    // Link-local (APIPA) and CGNAT/Tailscale-relay: EW never binds these.
                    if (b[0] == 169 && b[1] == 254)
                    {
                        return null;
                    }
                    if (b[0] == 100 && b[1] is >= 64 and <= 127)
                    {
                        return null;
                    }
                    if (b[0] == 127)
                    {
                        return 1; // loopback — last resort (ProcessProbe path)
                    }
                    if (b[0] == 10
                        || (b[0] == 172 && b[1] is >= 16 and <= 31)
                        || (b[0] == 192 && b[1] == 168))
                    {
                        return 40; // RFC1918 private — most preferred
                    }
                    return 30; // ordinary/site-local IPv4
                }

            case AddressFamily.InterNetworkV6:
                {
                    if (address.IsIPv6LinkLocal)
                    {
                        return null; // fe80::/10
                    }
                    if (IPAddress.IsLoopback(address))
                    {
                        return 1; // ::1 — last resort
                    }
                    return 20; // routable IPv6, ranked below any IPv4
                }

            default:
                return null;
        }
    }

    private static IReadOnlyList<IPAddress> DefaultResolveHostName(string host)
    {
        try
        {
            // ".local."/trailing-dot names often fail managed DNS; best-effort only.
            return Dns.GetHostAddresses(host.TrimEnd('.'));
        }
        catch (SocketException)
        {
            return Array.Empty<IPAddress>();
        }
        catch (ArgumentException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
