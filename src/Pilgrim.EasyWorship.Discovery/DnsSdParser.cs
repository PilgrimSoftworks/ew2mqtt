using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Defensive parser for Apple <c>dns-sd</c> stdout. Every dns-sd verb streams
/// forever, so output is always partial and may be interleaved across
/// interfaces — the methods here tolerate garbage, headers and duplicates.
/// </summary>
internal static partial class DnsSdParser
{
    // -B columns: Timestamp  A/R  Flags  if  Domain  ServiceType  InstanceName
    // The instance name is the remainder of the line and may contain spaces
    // and parentheses (e.g. "EasyWorship Remote (STUDIO)").
    [GeneratedRegex(
        @"^\s*\S+\s+(?<ar>Add|Rmv)\s+\S+\s+\S+\s+\S+\s+\S+\s+(?<name>.+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BrowseRowRegex();

    // -L success line: "<fqdn> can be reached at <host>:<port> (...)".
    [GeneratedRegex(
        @"can be reached at\s+(?<host>[^\s:]+):(?<port>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReachableRegex();

    /// <summary>
    /// Distinct instance names from <c>dns-sd -B</c> output, in first-seen
    /// order, with Bonjour escaping decoded. Only <c>Add</c> rows count;
    /// <c>Rmv</c> rows and headers are ignored.
    /// </summary>
    public static IReadOnlyList<string> ParseBrowse(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return Array.Empty<string>();
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> names = new();
        foreach (string line in SplitLines(stdout))
        {
            Match m = BrowseRowRegex().Match(line);
            if (!m.Success || !string.Equals(m.Groups["ar"].Value, "Add", StringComparison.Ordinal))
            {
                continue;
            }
            string name = Unescape(m.Groups["name"].Value);
            if (name.Length > 0 && seen.Add(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// First <c>host</c>/<c>port</c> from <c>dns-sd -L</c> output. There may be
    /// one line per interface; the first reachable wins. The trailing dot of a
    /// <c>.local.</c> host is trimmed.
    /// </summary>
    public static (string Host, int Port)? ParseResolve(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return null;
        }

        foreach (string line in SplitLines(stdout))
        {
            Match m = ReachableRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }
            if (!int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                || port is <= 0 or > 65535)
            {
                continue;
            }
            string host = Unescape(m.Groups["host"].Value).TrimEnd('.');
            if (host.Length == 0)
            {
                continue;
            }
            return (host, port);
        }
        return null;
    }

    /// <summary>
    /// First resolved IPv4 from <c>dns-sd -G v4</c> output (an <c>Add</c> row),
    /// or <c>null</c> if none was captured.
    /// </summary>
    public static IPAddress? ParseAddress(string stdout)
    {
        IReadOnlyList<IPAddress> all = ParseAddresses(stdout);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// All resolved IPv4 addresses from <c>dns-sd -G v4</c> output, in
    /// first-seen order and de-duplicated. A multi-homed host (LAN +
    /// Tailscale + Hyper-V/WSL vEthernet) answers <c>-G</c> with one
    /// <c>Add</c> row per interface; the reachable one is not necessarily
    /// first, so callers need the whole set, not just the head.
    /// </summary>
    public static IReadOnlyList<IPAddress> ParseAddresses(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return Array.Empty<IPAddress>();
        }

        HashSet<IPAddress> seen = new();
        List<IPAddress> addresses = new();
        foreach (string line in SplitLines(stdout))
        {
            if (TryParseAddressLine(line, out IPAddress? ip) && seen.Add(ip))
            {
                addresses.Add(ip);
            }
        }
        return addresses;
    }

    private static bool TryParseAddressLine(string line, out IPAddress address)
    {
        address = IPAddress.None;
        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isAdd = false;
        foreach (string t in tokens)
        {
            if (string.Equals(t, "Add", StringComparison.Ordinal))
            {
                isAdd = true;
                break;
            }
        }
        if (!isAdd)
        {
            return false;
        }
        foreach (string t in tokens)
        {
            // Require a canonical dotted quad: modern IPAddress.TryParse still
            // accepts oddities, and bare columns like the flags count must
            // never be mistaken for an address.
            if (t.Count(static c => c == '.') == 3
                && IPAddress.TryParse(t, out IPAddress? ip)
                && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                address = ip;
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the line is a <c>dns-sd -B</c> <c>Add</c> row.</summary>
    public static bool IsBrowseAddLine(string line)
    {
        Match m = BrowseRowRegex().Match(line);
        return m.Success && string.Equals(m.Groups["ar"].Value, "Add", StringComparison.Ordinal);
    }

    /// <summary>True if the line carries a <c>dns-sd -L</c> "reachable" address.</summary>
    public static bool IsReachableLine(string line)
        => ReachableRegex().IsMatch(line);

    /// <summary>True if the line is a <c>dns-sd -G</c> <c>Add</c> row with an IPv4.</summary>
    public static bool IsAddressLine(string line)
        => TryParseAddressLine(line, out _);

    /// <summary>
    /// Decode Bonjour's escaping: <c>\DDD</c> decimal byte escapes (e.g.
    /// <c>\032</c> = space) and single-character <c>\X</c> escapes (e.g.
    /// <c>\.</c>, <c>\\</c>).
    /// </summary>
    public static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\', StringComparison.Ordinal) < 0)
        {
            return value;
        }

        StringBuilder sb = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            if (i + 3 < value.Length
                && char.IsAsciiDigit(value[i + 1])
                && char.IsAsciiDigit(value[i + 2])
                && char.IsAsciiDigit(value[i + 3]))
            {
                int code = ((value[i + 1] - '0') * 100) + ((value[i + 2] - '0') * 10) + (value[i + 3] - '0');
                if (code is >= 0 and <= 255)
                {
                    sb.Append((char)code);
                    i += 3;
                    continue;
                }
            }

            sb.Append(value[i + 1]);
            i++;
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', StringSplitOptions.None)
               .Select(static l => l.TrimEnd('\r'));
}
