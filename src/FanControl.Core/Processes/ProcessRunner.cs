using System.ComponentModel;
using System.Diagnostics;

namespace FanControl.Core.Processes;

/// <summary>
/// Runs an external tool (nvidia-smi, smartctl) and captures its stdout, with a hard
/// timeout. Both tools talk to hardware and can block indefinitely when that hardware
/// misbehaves (a GPU that fell off the bus, a drive stuck in error recovery); without a
/// timeout that hang propagates straight into whichever loop awaited it. On timeout or
/// cancellation the child is killed rather than left behind.
/// </summary>
public static class ProcessRunner
{
    /// <returns>
    /// The tool's stdout and exit code, or null if it couldn't be started (missing, not
    /// executable) or didn't finish within <paramref name="timeout"/>.
    /// </returns>
    public static async Task<ProcessResult?> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
                await process.WaitForExitAsync(timeoutSource.Token);
                return new ProcessResult(process.ExitCode, output);
            }
            catch (OperationCanceledException)
            {
                Kill(process);

                // Shutdown propagates as cancellation; a timeout is just "no result".
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited, or can't be killed (stuck in uninterruptible I/O). Nothing more to do.
        }
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput);
