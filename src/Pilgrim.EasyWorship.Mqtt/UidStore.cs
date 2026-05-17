using Microsoft.Extensions.Logging;

namespace Pilgrim.EasyWorship.Mqtt;

internal sealed class UidStore
{
    private readonly ILogger<UidStore> _logger;

    public UidStore(ILogger<UidStore> logger)
    {
        _logger = logger;
    }

    public string LoadOrCreate(string? configuredUid)
    {
        if (!string.IsNullOrWhiteSpace(configuredUid))
        {
            return configuredUid;
        }

        string path = ResolvePath();
        if (File.Exists(path))
        {
            try
            {
                string content = File.ReadAllText(path).Trim();
                if (Guid.TryParse(content, out Guid existing))
                {
                    return existing.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to read UID file at {Path}", path);
            }
        }

        string uid = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, uid);
            _logger.LogInformation("Generated new UID {Uid} and persisted to {Path}", uid, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to persist UID to {Path}; using transient UID", path);
        }
        return uid;
    }

    private static string ResolvePath()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "ew2mqtt", "uid");
        }
        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "ew2mqtt", "uid");
        }
        string? stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(stateHome))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stateHome = Path.Combine(home, ".local", "state");
        }
        return Path.Combine(stateHome, "ew2mqtt", "uid");
    }
}
