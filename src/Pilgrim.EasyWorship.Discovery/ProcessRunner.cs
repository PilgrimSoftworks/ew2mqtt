using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pilgrim.EasyWorship.Discovery;

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, bool>? stopWhen = null)
    {
        ct.ThrowIfCancellationRequested();

        using CancellationTokenSource satisfiedCts = new();

        ProcessStartInfo psi = new()
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = new() { StartInfo = psi };

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }
            if (stopWhen is not null && stopWhen(e.Data) && !satisfiedCts.IsCancellationRequested)
            {
                satisfiedCts.Cancel();
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessRunResult(null, string.Empty, string.Empty, TimedOut: false);
            }
        }
        catch (Win32Exception ex)
        {
            // Binary missing/not executable — caller falls through to Zeroconf.
            return new ProcessRunResult(null, string.Empty, ex.Message, TimedOut: false);
        }
        catch (InvalidOperationException ex)
        {
            return new ProcessRunResult(null, string.Empty, ex.Message, TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, satisfiedCts.Token);
        if (timeout > TimeSpan.Zero)
        {
            waitCts.CancelAfter(timeout);
        }
        else
        {
            waitCts.Cancel();
        }

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // dns-sd streams forever, so the expected paths are either our
            // stop-predicate matching (got the answer) or the budget elapsing
            // — never the caller cancelling.
            timedOut = !ct.IsCancellationRequested && !satisfiedCts.IsCancellationRequested;
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Process already gone.
            }
        }

        // Drain any buffered async output the redirected readers haven't
        // flushed yet, then stop the readers.
        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        process.CancelOutputRead();
        process.CancelErrorRead();

        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            // No exit code available (killed before it was set).
        }

        string outText;
        string errText;
        lock (stdout)
        {
            outText = stdout.ToString();
        }
        lock (stderr)
        {
            errText = stderr.ToString();
        }

        // A real caller cancellation propagates; our internal timeout does not.
        if (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }

        return new ProcessRunResult(exitCode, outText, errText, timedOut);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill.
        }
        catch (Win32Exception)
        {
            // Access denied / already terminating — best effort.
        }
        catch (NotSupportedException)
        {
            // Platform cannot kill the tree — best effort.
        }
    }
}
