using System.Net;

namespace Pilgrim.EasyWorship.Discovery;

internal interface IZeroconfShim
{
    Task<IReadOnlyList<DiscoveredService>> ResolveAsync(
        string serviceType,
        TimeSpan scanTime,
        CancellationToken ct);
}

internal sealed record DiscoveredService(
    string DisplayName,
    string Hostname,
    int Port,
    IReadOnlyList<IPAddress> Addresses);
