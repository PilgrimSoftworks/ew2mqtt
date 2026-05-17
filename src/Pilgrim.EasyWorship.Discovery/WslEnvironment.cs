using System.Net;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Detects WSL and exposes the Windows host particulars that matter for
/// discovery: EasyWorship and Bonjour run on the Windows side, so from a WSL2
/// distro we reach them through interop (<c>dns-sd.exe</c>, <c>netstat.exe</c>)
/// and connect via the Windows host IP, not loopback.
/// </summary>
internal static class WslEnvironment
{
    /// <summary>True when running inside a WSL distribution.</summary>
    public static bool IsWsl { get; } = DetectWsl();

    /// <summary>
    /// <c>/mnt</c> mount points that look like Windows drives (the automount
    /// root defaults to <c>/mnt</c>; each drive letter is a sub-directory).
    /// </summary>
    public static IReadOnlyList<string> WindowsDriveRoots()
    {
        const string automount = "/mnt";
        try
        {
            if (!Directory.Exists(automount))
            {
                return Array.Empty<string>();
            }
            // Drive letters are single-character dirs (c, d, ...). Skip wsl/wslg.
            return Directory.EnumerateDirectories(automount)
                .Where(static d => Path.GetFileName(d).Length == 1)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Best-effort Windows host address as seen from WSL2's NAT network: the
    /// default-route gateway, falling back to the <c>resolv.conf</c> nameserver,
    /// then loopback (correct under WSL "mirrored" networking). Used only by the
    /// last-resort process probe; the dns-sd path yields the real LAN IP.
    /// </summary>
    public static string ResolveWindowsHost()
    {
        string? gateway = ReadDefaultGateway();
        if (!string.IsNullOrEmpty(gateway))
        {
            return gateway;
        }
        string? nameserver = ReadResolvConfNameserver();
        return !string.IsNullOrEmpty(nameserver) ? nameserver : "127.0.0.1";
    }

    /// <summary>
    /// WSL2 NAT-side Windows-host candidates — the default-route gateway and
    /// the <c>resolv.conf</c> nameserver — as parsed IPs, gateway first,
    /// de-duped. Empty when not under WSL. Loopback is intentionally excluded:
    /// the reachability selector tries <c>127.0.0.1</c> unconditionally as a
    /// last resort (correct under WSL "mirrored" networking), so these are only
    /// the host addresses that matter under WSL2 NAT networking.
    /// </summary>
    public static IReadOnlyList<IPAddress> WindowsHostCandidates()
    {
        if (!IsWsl)
        {
            return Array.Empty<IPAddress>();
        }

        List<IPAddress> result = new(2);
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value)
                && IPAddress.TryParse(value, out IPAddress? ip)
                && !result.Contains(ip))
            {
                result.Add(ip);
            }
        }

        Add(ReadDefaultGateway());
        Add(ReadResolvConfNameserver());
        return result;
    }

    private static bool DetectWsl()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
        {
            return true;
        }
        foreach (string path in (string[])["/proc/sys/kernel/osrelease", "/proc/version"])
        {
            try
            {
                if (File.Exists(path)
                    && File.ReadAllText(path).Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Unreadable /proc entry — try the next signal.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    private static string? ReadDefaultGateway()
    {
        // /proc/net/route columns: Iface Destination Gateway Flags RefCnt Use
        // Metric Mask MTU Window IRTT. The default route has Destination
        // 00000000 and Gateway as little-endian hex of the IPv4. With several
        // default routes (e.g. a VPN alongside the WSL NAT link) prefer the
        // lowest Metric — that's the route the kernel actually uses.
        try
        {
            string? best = null;
            long bestMetric = long.MaxValue;
            foreach (string? line in File.ReadLines("/proc/net/route").Skip(1))
            {
                string[] f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (f.Length > 6
                    && string.Equals(f[1], "00000000", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(f[2], System.Globalization.NumberStyles.HexNumber, null, out uint gw)
                    && gw != 0)
                {
                    long metric = long.TryParse(f[6], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long m) ? m : long.MaxValue;
                    if (best is null || metric < bestMetric)
                    {
                        best = $"{gw & 0xFF}.{(gw >> 8) & 0xFF}.{(gw >> 16) & 0xFF}.{(gw >> 24) & 0xFF}";
                        bestMetric = metric;
                    }
                }
            }
            return best;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static string? ReadResolvConfNameserver()
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/resolv.conf"))
            {
                string t = line.Trim();
                if (t.StartsWith("nameserver ", StringComparison.Ordinal))
                {
                    return t["nameserver ".Length..].Trim();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
}
