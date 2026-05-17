namespace Pilgrim.EasyWorship.Discovery.Tests;

/// <summary>
/// Canned <see cref="IProcessRunner"/>: dispatches on the dns-sd verb
/// (<c>-B</c>/<c>-L</c>/<c>-G</c>) and records every invocation's argument list.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<IReadOnlyList<string>, ProcessRunResult> _handler;

    public FakeProcessRunner(Func<IReadOnlyList<string>, ProcessRunResult> handler)
        => _handler = handler;

    public List<IReadOnlyList<string>> Invocations { get; } = [];

    public Task<ProcessRunResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, bool>? stopWhen = null)
    {
        ct.ThrowIfCancellationRequested();
        Invocations.Add(args);
        // Canned output is returned whole and instantly, so the early-stop
        // predicate is irrelevant here.
        return Task.FromResult(_handler(args));
    }

    public static ProcessRunResult Streamed(string stdout)
        => new(ExitCode: null, stdout, string.Empty, TimedOut: true);

    public static ProcessRunResult Empty()
        => new(ExitCode: null, string.Empty, string.Empty, TimedOut: true);
}
