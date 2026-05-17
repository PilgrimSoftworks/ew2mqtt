namespace Pilgrim.EasyWorship.Discovery;

public interface IEasyWorshipDiscovery
{
    public const string ServiceType = "_ezwremote._tcp.local.";

    IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(TimeSpan window, CancellationToken ct = default);
    Task<EasyWorshipEndpoint?> ResolveOnceAsync(TimeSpan timeout, CancellationToken ct = default);
}
