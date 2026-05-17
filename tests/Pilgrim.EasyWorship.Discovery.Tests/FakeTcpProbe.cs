using System.Net;

namespace Pilgrim.EasyWorship.Discovery.Tests;

/// <summary>
/// Canned <see cref="ITcpProbe"/>: a delegate decides reachability and every
/// attempt is recorded, mirroring the <see cref="FakeProcessRunner"/> pattern.
/// </summary>
internal sealed class FakeTcpProbe : ITcpProbe
{
    private readonly Func<IPAddress, int, bool> _reachable;

    public FakeTcpProbe(Func<IPAddress, int, bool> reachable)
        => _reachable = reachable;

    public List<(IPAddress Address, int Port)> Attempts { get; } = [];

    /// <summary>Only the listed addresses (string form) accept a connection.</summary>
    public static FakeTcpProbe Reachable(params string[] addresses)
    {
        HashSet<string> set = new(addresses, StringComparer.OrdinalIgnoreCase);
        return new FakeTcpProbe((a, _) => set.Contains(a.ToString()));
    }

    /// <summary>Nothing answers — exercises the fall-back-to-original path.</summary>
    public static FakeTcpProbe None()
        => new((_, _) => false);

    public Task<bool> CanConnectAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attempts.Add((address, port));
        return Task.FromResult(_reachable(address, port));
    }
}
