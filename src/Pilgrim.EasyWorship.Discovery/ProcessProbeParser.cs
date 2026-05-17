using System.Globalization;
using System.Text.RegularExpressions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Defensive parsers for the same-machine process/port probe. EasyWorship can
/// spawn several processes (updater, helpers); only the one actually
/// <c>LISTEN</c>ing on a TCP port is the ezwremote endpoint.
/// </summary>
internal static partial class ProcessProbeParser
{
    public const string ProcessNameNeedle = "easyworship";

    // Windows netstat -ano -p TCP row:
    //   TCP    0.0.0.0:9888   0.0.0.0:0   LISTENING   12345
    //   TCP    [::]:9888      [::]:0      LISTENING   12345
    [GeneratedRegex(
        @"^\s*TCP\s+\S+:(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NetstatListenRegex();

    // Linux `ss -tlnp` row:
    //   LISTEN 0 128 0.0.0.0:9888 0.0.0.0:* users:(("EasyWorship",pid=1234,fd=7))
    [GeneratedRegex(
        @"^LISTEN\s+\S+\s+\S+\s+\S+:(?<port>\d+)\s+\S+(?<users>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SsListenRegex();

    [GeneratedRegex(
        @"\(\""(?<name>[^""]+)"",pid=(?<pid>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SsUsersRegex();

    /// <summary>
    /// PIDs from <c>tasklist /FO CSV /NH</c> whose image name contains
    /// "easyworship" (case-insensitive).
    /// </summary>
    public static IReadOnlySet<int> ParseEasyWorshipPids(string tasklistCsv)
    {
        HashSet<int> pids = new();
        if (string.IsNullOrEmpty(tasklistCsv))
        {
            return pids;
        }

        foreach (string line in SplitLines(tasklistCsv))
        {
            List<string> cols = SplitCsv(line);
            if (cols.Count < 2)
            {
                continue;
            }
            if (cols[0].Contains(ProcessNameNeedle, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(cols[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                pids.Add(pid);
            }
        }
        return pids;
    }

    /// <summary>
    /// Listening <c>(port, pid)</c> pairs from Windows <c>netstat -ano</c>.
    /// </summary>
    public static IReadOnlyList<(int Port, int Pid)> ParseNetstatListeners(string netstat)
    {
        List<(int, int)> result = new();
        if (string.IsNullOrEmpty(netstat))
        {
            return result;
        }

        foreach (string line in SplitLines(netstat))
        {
            Match m = NetstatListenRegex().Match(line);
            if (m.Success
                && TryPort(m.Groups["port"].Value, out int port)
                && int.TryParse(m.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                result.Add((port, pid));
            }
        }
        return result;
    }

    /// <summary>
    /// Listening ports owned by an EasyWorship process from Linux
    /// <c>ss -tlnp</c> (the process name is embedded in the row).
    /// </summary>
    public static IReadOnlyList<int> ParseSsEasyWorshipPorts(string ss)
    {
        List<int> ports = new();
        if (string.IsNullOrEmpty(ss))
        {
            return ports;
        }

        foreach (string line in SplitLines(ss))
        {
            Match m = SsListenRegex().Match(line);
            if (!m.Success || !TryPort(m.Groups["port"].Value, out int port))
            {
                continue;
            }
            foreach (Match u in SsUsersRegex().Matches(m.Groups["users"].Value))
            {
                if (u.Groups["name"].Value.Contains(ProcessNameNeedle, StringComparison.OrdinalIgnoreCase))
                {
                    ports.Add(port);
                    break;
                }
            }
        }
        return ports;
    }

    /// <summary>
    /// Pick the endpoint port from the EasyWorship-owned listening ports.
    /// EasyWorship's ezwremote listener uses a <em>dynamic</em> port (there is
    /// no well-known/default port to prefer), and in practice the process has
    /// exactly one listener. If several are seen, the lowest is chosen
    /// deterministically — <paramref name="ambiguous"/> reports that case so
    /// the caller can log it.
    /// </summary>
    public static int? ChoosePort(IEnumerable<int> ports, out bool ambiguous)
    {
        int[] distinct = ports.Where(static p => p is > 0 and <= 65535).Distinct().ToArray();
        ambiguous = distinct.Length > 1;
        return distinct.Length == 0 ? null : distinct.Min();
    }

    private static bool TryPort(string value, out int port)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
           && port is > 0 and <= 65535;

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', StringSplitOptions.None).Select(static l => l.TrimEnd('\r'));

    private static List<string> SplitCsv(string line)
    {
        // tasklist quotes every field and values never contain embedded quotes
        // or commas, so this stays simple — but an unquoted field (localized or
        // future format variation) is read to the next comma rather than
        // truncating the rest of the row.
        List<string> cols = new();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    break;
                }
                cols.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int comma = line.IndexOf(',', i);
                if (comma < 0)
                {
                    cols.Add(line[i..]);
                    break;
                }
                cols.Add(line[i..comma]);
                i = comma;
            }
            if (i < line.Length && line[i] == ',')
            {
                i++;
            }
        }
        return cols;
    }
}
