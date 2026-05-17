using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pilgrim.EasyWorship.Options;
using Pilgrim.EasyWorship.Transport;

namespace Pilgrim.EasyWorship;

public static class EasyWorshipServiceCollectionExtensions
{
    public static IServiceCollection AddEasyWorshipClient(
        this IServiceCollection services,
        Action<EasyWorshipClientOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEzwTransportFactory, TcpEzwTransportFactory>();
        services.TryAddSingleton<IEasyWorshipClient>(sp =>
        {
            IOptions<EasyWorshipClientOptions> options = sp.GetRequiredService<IOptions<EasyWorshipClientOptions>>();
            IEzwTransportFactory factory = sp.GetRequiredService<IEzwTransportFactory>();
            TimeProvider time = sp.GetRequiredService<TimeProvider>();
            ILogger<EasyWorshipClient>? logger = sp.GetService<ILogger<EasyWorshipClient>>();
            return new EasyWorshipClient(options, factory, time, logger);
        });
        return services;
    }
}
