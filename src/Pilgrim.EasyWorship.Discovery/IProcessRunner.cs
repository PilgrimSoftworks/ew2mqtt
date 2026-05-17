namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Thin, injectable wrapper around launching a child process and capturing its
/// output. Mirrors the <see cref="IZeroconfShim"/> pattern so the dns-sd path
/// can be unit-tested with canned output.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="file"/> with <paramref name="args"/>, redirecting
    /// stdout/stderr. The process is killed when <paramref name="timeout"/>
    /// elapses (dns-sd streams forever and never exits on its own), and
    /// whatever was captured up to that point is returned. If
    /// <paramref name="stopWhen"/> is supplied it is evaluated per captured
    /// stdout line; the first line it matches kills the process early (so a
    /// streaming verb returns as soon as the answer arrives instead of waiting
    /// out the whole budget). Only a genuine cancellation via
    /// <paramref name="ct"/> throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, bool>? stopWhen = null);
}

/// <param name="ExitCode">
/// Process exit code, or <c>null</c> when the process was killed on timeout or
/// never started.
/// </param>
/// <param name="StandardOutput">Captured stdout (may be partial).</param>
/// <param name="StandardError">Captured stderr (may be partial).</param>
/// <param name="TimedOut">
/// True when the process was killed because <c>timeout</c> elapsed (the normal
/// case for streaming dns-sd verbs).
/// </param>
internal sealed record ProcessRunResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);
