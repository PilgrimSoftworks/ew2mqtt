using System.Net;

using Zeroconf;

namespace Pilgrim.EasyWorship.Discovery;

internal sealed class ZeroconfShim : IZeroconfShim
{
    public async Task<IReadOnlyList<DiscoveredService>> ResolveAsync(
        string serviceType,
        TimeSpan scanTime,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IZeroconfHost> hosts = await ZeroconfResolver.ResolveAsync(serviceType, scanTime, cancellationToken: ct).ConfigureAwait(false);
        List<DiscoveredService> results = new(hosts.Count);
        foreach (IZeroconfHost? host in hosts)
        {
            if (!host.Services.TryGetValue(serviceType, out IService? service) || service is null)
            {
                continue;
            }
            IPAddress[] addresses = host.IPAddresses
                .Where(static s => !string.IsNullOrEmpty(s))
                .Select(s => IPAddress.TryParse(s, out IPAddress? ip) ? ip : null)
                .Where(ip => ip is not null)
                .Cast<IPAddress>()
                .ToArray();

            results.Add(new DiscoveredService(
                DisplayName: host.DisplayName,
                Hostname: host.IPAddress,
                Port: service.Port,
                Addresses: addresses));
        }
        return results;
    }
}
