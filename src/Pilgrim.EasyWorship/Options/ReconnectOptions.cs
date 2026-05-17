namespace Pilgrim.EasyWorship.Options;

public sealed class ReconnectOptions
{
    public int Phase1InitialMs { get; set; } = 1000;
    public double Phase1Multiplier { get; set; } = 1.5;
    public int Phase1CapMs { get; set; } = 5000;
    public int Phase1DurationSeconds { get; set; } = 180;
    public int Phase2IntervalSeconds { get; set; } = 30;

    public TimeSpan Phase1Initial => TimeSpan.FromMilliseconds(Phase1InitialMs);
    public TimeSpan Phase1Cap => TimeSpan.FromMilliseconds(Phase1CapMs);
    public TimeSpan Phase1Duration => TimeSpan.FromSeconds(Phase1DurationSeconds);
    public TimeSpan Phase2Interval => TimeSpan.FromSeconds(Phase2IntervalSeconds);
}
