using FanControl.Core.Processes;
using Xunit;

namespace FanControl.Core.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ReturnsNullWhenTheExecutableDoesNotExist()
    {
        var result = await ProcessRunner.RunAsync("definitely-not-a-real-tool-4f1c", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CapturesStdoutAndExitCode()
    {
        // dotnet is the one executable guaranteed to be on PATH wherever these tests can run at all.
        var result = await ProcessRunner.RunAsync("dotnet", ["--version"], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
    }

    [Fact]
    public async Task KillsTheProcessAndReturnsNullOnTimeout()
    {
        // No process can start, run and exit inside a millisecond, so this always times out.
        var started = DateTime.UtcNow;
        var result = await ProcessRunner.RunAsync("dotnet", ["--version"], TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Null(result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task PropagatesCallerCancellationRatherThanReportingATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("dotnet", ["--version"], TimeSpan.FromSeconds(30), cancelled.Token));
    }
}
