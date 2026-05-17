using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Tries each underlying <see cref="IEasyWorshipDiscovery"/> in order and uses
/// the first that produces anything. This lets a present-but-unhelpful
/// <c>dns-sd</c> (e.g. Bonjour installed but the service stopped) fall through
/// to the managed Zeroconf path. A provider that throws is logged and skipped;
/// only genuine caller cancellation propagates.
/// </summary>
public sealed class CompositeEasyWorshipDiscovery : IEasyWorshipDiscovery
{
    // Upper bound on the slice withheld from providers so a reachable address
    // can still be probe-selected even when a provider spends most of the
    // budget. Bounded so it never starves discovery on a tight timeout.
    private static readonly TimeSpan MaxProbeReserve = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IEasyWorshipDiscovery> _providers;
    private readonly ReachableAddressSelector _selector;
    private readonly ILogger<CompositeEasyWorshipDiscovery> _logger;

    public CompositeEasyWorshipDiscovery(
        IEnumerable<IEasyWorshipDiscovery> providers,
        ILogger<CompositeEasyWorshipDiscovery>? logger = null)
        : this(providers, new TcpProbe(), logger)
    {
    }

    internal CompositeEasyWorshipDiscovery(
        IEnumerable<IEasyWorshipDiscovery> providers,
        ITcpProbe probe,
        ILogger<CompositeEasyWorshipDiscovery>? logger)
    {
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? NullLogger<CompositeEasyWorshipDiscovery>.Instance;
        _selector = new ReachableAddressSelector(probe, _logger);
    }

    public async IAsyncEnumerable<EasyWorshipEndpoint> BrowseAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + window;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (IEasyWorshipDiscovery provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                yield break;
            }

            bool producedAny = false;
            await using IAsyncEnumerator<EasyWorshipEndpoint> enumerator = provider
                .BrowseAsync(remaining, ct)
                .GetAsyncEnumerator(ct);

            while (true)
            {
                EasyWorshipEndpoint current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // a misbehaving provider must not break the chain
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogDebug(
                        ex, "Discovery provider {Provider} failed; falling through",
                        provider.GetType().Name);
                    break;
                }

                string key = $"{current.InstanceName}|{current.Host}|{current.Port}";
                if (!seen.Add(key))
                {
                    continue;
                }
                producedAny = true;
                yield return current;
            }

            if (producedAny)
            {
                yield break;
            }
        }
    }

    public async Task<EasyWorshipEndpoint?> ResolveOnceAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        // Withhold a slice from providers so the winning endpoint can still be
        // connect-probed within the caller's timeout. Capping the provider call
        // (rather than extending past the deadline) keeps probing bounded.
        TimeSpan probeReserve = Clamp(
            TimeSpan.FromTicks(timeout.Ticks / 4),
            ReachableAddressSelector.DefaultPerAddressTimeout,
            MaxProbeReserve);

        foreach (IEasyWorshipDiscovery provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // On a budget too tight to reserve, let the provider use it all and
            // let selection take whatever is left (its probe is self-bounded).
            TimeSpan providerBudget = remaining - probeReserve;
            if (providerBudget <= TimeSpan.Zero)
            {
                providerBudget = remaining;
            }

            try
            {
                EasyWorshipEndpoint? endpoint = await provider.ResolveOnceAsync(providerBudget, ct).ConfigureAwait(false);
                if (endpoint is not null)
                {
                    TimeSpan selectBudget = deadline - DateTimeOffset.UtcNow;
                    if (selectBudget > TimeSpan.Zero)
                    {
                        endpoint = await _selector
                            .SelectAsync(endpoint, selectBudget, ct)
                            .ConfigureAwait(false);
                    }
                    return endpoint;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // a misbehaving provider must not break the chain
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(
                    ex, "Discovery provider {Provider} failed; falling through",
                    provider.GetType().Name);
            }
        }
        return null;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            max = min;
        }
        if (value < min)
        {
            return min;
        }
        return value > max ? max : value;
    }
}
