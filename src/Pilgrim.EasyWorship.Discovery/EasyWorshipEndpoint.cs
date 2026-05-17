using System.Net;

namespace Pilgrim.EasyWorship.Discovery;

public sealed record EasyWorshipEndpoint(
    string InstanceName,
    string Host,
    int Port,
    IReadOnlyList<IPAddress> Addresses);
