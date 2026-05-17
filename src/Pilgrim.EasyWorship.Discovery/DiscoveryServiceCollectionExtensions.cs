using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Pilgrim.EasyWorship.Discovery;

public static class DiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-aware <see cref="IEasyWorshipDiscovery"/> as a
    /// composite chain tried in order:
    /// <list type="number">
    ///   <item>Bonjour <c>dns-sd</c> — only when the binary is present
    ///   (Windows with Bonjour, macOS always, Linux with avahi-compat, or WSL
    ///   reaching the Windows <c>dns-sd.exe</c> via interop);</item>
    ///   <item>the managed Zeroconf resolver;</item>
    ///   <item>a same-machine EasyWorship process/port probe (under WSL this
    ///   probes the Windows host through interop).</item>
    /// </list>
    /// Uses <c>TryAddSingleton</c> so a consumer can still override by
    /// registering their own implementation first.
    /// </summary>
    public static IServiceCollection AddEasyWorshipDiscovery(this IServiceCollection services)
    {
        services.TryAddSingleton<IEasyWorshipDiscovery>(static sp =>
        {
            ILoggerFactory? loggerFactory = sp.GetService<ILoggerFactory>();

            List<IEasyWorshipDiscovery> providers = new();

            string? dnsSd = DnsSdLocator.Find();
            if (dnsSd is not null)
            {
                providers.Add(new BonjourEasyWorshipDiscovery(
                    dnsSd, loggerFactory?.CreateLogger<BonjourEasyWorshipDiscovery>()));
            }

            providers.Add(new ZeroconfEasyWorshipDiscovery(
                loggerFactory?.CreateLogger<ZeroconfEasyWorshipDiscovery>()));

            providers.Add(new ProcessProbeEasyWorshipDiscovery(
                loggerFactory?.CreateLogger<ProcessProbeEasyWorshipDiscovery>()));

            return new CompositeEasyWorshipDiscovery(
                providers,
                loggerFactory?.CreateLogger<CompositeEasyWorshipDiscovery>());
        });
        return services;
    }
}
