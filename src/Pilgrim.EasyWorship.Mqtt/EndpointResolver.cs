using Microsoft.Extensions.Logging;
using Pilgrim.EasyWorship.Discovery;
using Pilgrim.EasyWorship.Mqtt.Options;

namespace Pilgrim.EasyWorship.Mqtt;

internal sealed class EndpointResolver
{
    private readonly IEasyWorshipDiscovery _discovery;
    private readonly ILogger<EndpointResolver> _logger;

    public EndpointResolver(IEasyWorshipDiscovery discovery, ILogger<EndpointResolver> logger)
    {
        _discovery = discovery;
        _logger = logger;
    }

    public async Task<(string Host, int Port)?> ResolveAsync(EasyWorshipServiceOptions options, CancellationToken ct)
    {
        if (options.DiscoveryMode == DiscoveryMode.Manual)
        {
            if (TryManualEndpoint(options, out (string Host, int Port) manual))
            {
                return manual;
            }
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                _logger.LogError(
                    "DiscoveryMode=Manual but no EasyWorship Host configured (Ew2Mqtt:EasyWorship:Host/Port).");
            }
            return null;
        }

        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, options.DiscoveryTimeoutSeconds));
        try
        {
            _logger.LogInformation("Browsing mDNS for EasyWorship instance ({Timeout}s)", timeout.TotalSeconds);
            EasyWorshipEndpoint? endpoint = await _discovery.ResolveOnceAsync(timeout, ct).ConfigureAwait(false);
            if (endpoint is not null)
            {
                _logger.LogInformation("Discovered EasyWorship at {Host}:{Port} ({Name})",
                    endpoint.Host, endpoint.Port, endpoint.InstanceName);
                return (endpoint.Host, endpoint.Port);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS discovery failed");
        }

        if (options.DiscoveryMode == DiscoveryMode.AutoThenManual
            && TryManualEndpoint(options, out (string Host, int Port) fallback))
        {
            _logger.LogInformation(
                "Falling back to configured EasyWorship endpoint {Host}:{Port}",
                fallback.Host, fallback.Port);
            return fallback;
        }

        return null;
    }

    /// <summary>
    /// A manual endpoint is usable only when both Host and an explicit, valid
    /// Port are configured. EasyWorship uses a dynamic ezwremote port, so a
    /// missing port is a configuration error, not something to default.
    /// </summary>
    private bool TryManualEndpoint(EasyWorshipServiceOptions options, out (string Host, int Port) endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return false;
        }
        if (options.Port is not (> 0 and <= 65535))
        {
            _logger.LogError(
                "EasyWorship Host '{Host}' is set but Port is not (Ew2Mqtt:EasyWorship:Port). "
                + "EasyWorship listens on a dynamic port — set an explicit Port or use Auto discovery.",
                options.Host);
            return false;
        }
        endpoint = (options.Host!, options.Port.Value);
        return true;
    }
}
