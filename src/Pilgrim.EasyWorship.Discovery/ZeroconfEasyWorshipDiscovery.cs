using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pilgrim.EasyWorship.Discovery;

public sealed class ZeroconfEasyWorshipDiscovery : IEasyWorshipDiscovery
{
    private readonly IZeroconfShim _shim;
    private readonly ILogger<ZeroconfEasyWorshipDiscovery> _logger;

    public ZeroconfEasyWorshipDiscovery(ILogger<ZeroconfEasyWorshipDiscovery>? logger = null)
        : this(new ZeroconfShim(), logger)
    {
    }

    internal ZeroconfEasyWorshipDiscovery(
        IZeroconfShim shim,
        ILogger<ZeroconfEasyWorshipDiscovery>? logger)
    {
        _shim = shim;
        _logger = logger ?? NullLogger<ZeroconfEasyWorshipDiscovery>.Instance;
    }

    public async IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DiscoveredService> services = await _shim.ResolveAsync(IEasyWorshipDiscovery.ServiceType, window, ct).ConfigureAwait(false);
        foreach (DiscoveredService svc in services)
        {
            ct.ThrowIfCancellationRequested();
            string key = $"{svc.DisplayName}|{svc.Hostname}|{svc.Port}";
            if (!seen.Add(key))
            {
                continue;
            }
            _logger.LogDebug("Discovered EasyWorship instance: {Name} at {Host}:{Port}", svc.DisplayName, svc.Hostname, svc.Port);
            yield return new EasyWorshipEndpoint(svc.DisplayName, svc.Hostname, svc.Port, svc.Addresses);
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
}
