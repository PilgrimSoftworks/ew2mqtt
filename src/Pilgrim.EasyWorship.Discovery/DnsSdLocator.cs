namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Locates Apple's <c>dns-sd</c> binary. Presence of the binary — not just the
/// OS — is what decides whether the Bonjour path is wired in: macOS always
/// ships it, Windows has it only when Bonjour is installed, Linux only with
/// <c>avahi-utils</c>/avahi-compat.
/// </summary>
internal static class DnsSdLocator
{
    public static string? Find()
    {
        // Probe order is by binary, not OS: native dns-sd first, then — under
        // WSL — the Windows dns-sd.exe via interop (EasyWorship + Bonjour live
        // on the Windows host, so dns-sd.exe browses the right network stack).
        foreach (string exe in CandidateExeNames())
        {
            string? onPath = FindOnPath(exe);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        foreach (string candidate in WellKnownLocations())
        {
            if (SafeFileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateExeNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "dns-sd.exe";
            yield break;
        }
        yield return "dns-sd";
        if (WslEnvironment.IsWsl)
        {
            yield return "dns-sd.exe";
        }
    }

    private static IEnumerable<string> WellKnownLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, "Bonjour", "dns-sd.exe");
            }

            string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Bonjour", "dns-sd.exe");
            }

            string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrEmpty(systemRoot))
            {
                yield return Path.Combine(systemRoot, "System32", "dns-sd.exe");
            }
            yield return @"C:\Windows\System32\dns-sd.exe";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/bin/dns-sd";
        }
        else
        {
            // Linux (incl. WSL): avahi-compat dns-sd typically lands on PATH.
            yield return "/usr/bin/dns-sd";
            yield return "/usr/local/bin/dns-sd";

            // Under WSL the real win is the Windows Bonjour binary reached
            // through the drive mounts, even when it isn't on PATH.
            if (WslEnvironment.IsWsl)
            {
                foreach (string drive in WslEnvironment.WindowsDriveRoots())
                {
                    yield return Path.Combine(drive, "Program Files", "Bonjour", "dns-sd.exe");
                    yield return Path.Combine(drive, "Program Files (x86)", "Bonjour", "dns-sd.exe");
                    yield return Path.Combine(drive, "Windows", "System32", "dns-sd.exe");
                }
            }
        }
    }

    private static string? FindOnPath(string exe)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string dir in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir, exe);
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip it.
                continue;
            }
            if (SafeFileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
