using Pilgrim.EasyWorship.Options;

namespace Pilgrim.EasyWorship.State;

internal sealed class BackoffSchedule
{
    private readonly ReconnectOptions _options;
    private int _attempt;
    private TimeSpan _phase1Elapsed;

    public BackoffSchedule(ReconnectOptions options)
    {
        _options = options;
    }

    public TimeSpan NextDelay()
    {
        TimeSpan delay;
        if (_phase1Elapsed < _options.Phase1Duration)
        {
            double ms = _options.Phase1InitialMs * Math.Pow(_options.Phase1Multiplier, _attempt);
            ms = Math.Min(ms, _options.Phase1CapMs);
            delay = TimeSpan.FromMilliseconds(ms);
            _phase1Elapsed += delay;
        }
        else
        {
            delay = _options.Phase2Interval;
        }

        _attempt++;
        return delay;
    }

    public void Reset()
    {
        _attempt = 0;
        _phase1Elapsed = TimeSpan.Zero;
    }
}
