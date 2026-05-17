using Pilgrim.EasyWorship.Options;
using Pilgrim.EasyWorship.State;

namespace Pilgrim.EasyWorship.Tests.State;

public sealed class BackoffScheduleTests
{
    [Test]
    public async Task Phase1_grows_exponentially_until_cap()
    {
        BackoffSchedule schedule = new(new ReconnectOptions
        {
            Phase1InitialMs = 1000,
            Phase1Multiplier = 1.5,
            Phase1CapMs = 5000,
            Phase1DurationSeconds = 600,
            Phase2IntervalSeconds = 30,
        });

        TimeSpan d0 = schedule.NextDelay();
        TimeSpan d1 = schedule.NextDelay();
        TimeSpan d2 = schedule.NextDelay();
        TimeSpan d3 = schedule.NextDelay();
        TimeSpan d4 = schedule.NextDelay();
        TimeSpan d5 = schedule.NextDelay();

        await Assert.That(d0).IsEqualTo(TimeSpan.FromMilliseconds(1000));
        await Assert.That(d1).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(d2).IsEqualTo(TimeSpan.FromMilliseconds(2250));
        await Assert.That(d3).IsEqualTo(TimeSpan.FromMilliseconds(3375));
        await Assert.That(d4).IsEqualTo(TimeSpan.FromMilliseconds(5000));
        await Assert.That(d5).IsEqualTo(TimeSpan.FromMilliseconds(5000));
    }

    [Test]
    public async Task Switches_to_phase2_after_duration_elapsed()
    {
        BackoffSchedule schedule = new(new ReconnectOptions
        {
            Phase1InitialMs = 1000,
            Phase1Multiplier = 1.5,
            Phase1CapMs = 5000,
            Phase1DurationSeconds = 10,
            Phase2IntervalSeconds = 30,
        });

        // First few attempts cumulate ~12s of phase1 wait → switch to phase2
        TimeSpan last = TimeSpan.Zero;
        for (int i = 0; i < 10; i++)
        {
            last = schedule.NextDelay();
        }

        await Assert.That(last).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Reset_returns_to_initial_phase1_delay()
    {
        BackoffSchedule schedule = new(new ReconnectOptions
        {
            Phase1InitialMs = 1000,
            Phase1Multiplier = 2,
            Phase1CapMs = 8000,
            Phase1DurationSeconds = 600,
        });

        for (int i = 0; i < 5; i++)
        {
            schedule.NextDelay();
        }

        schedule.Reset();
        TimeSpan first = schedule.NextDelay();

        await Assert.That(first).IsEqualTo(TimeSpan.FromMilliseconds(1000));
    }
}
