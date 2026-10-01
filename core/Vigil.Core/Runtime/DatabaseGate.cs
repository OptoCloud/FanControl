using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vigil.Core.Runtime;

/// <summary>
/// The database as the runtime sees it: up, or down until the next retry. The live view must
/// work without a database (ARCHITECTURE.md invariant 5), so a failure here is logged and
/// retried, never thrown.
/// </summary>
/// <remarks>
/// Npgsql's pool would reconnect by itself, but each attempt against a host that is down waits
/// out the connect timeout, and the runtime is the one loop that feeds the live view: trying
/// on every snapshot would freeze the dashboard for as long as the database is gone. So a lost
/// connection closes the gate until <see cref="RetryAfter"/>, as the Rust core did.
/// </remarks>
public sealed class DatabaseGate(ILogger logger, TimeProvider time)
{
    /// <summary>How long a database that failed to connect is left alone.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    private long _nextAttempt;
    private string _lastError = string.Empty;

    public bool IsReady { get; private set; }

    /// <summary>Down, and the retry delay has passed.</summary>
    public bool IsDue => !IsReady && time.GetTimestamp() >= _nextAttempt;

    /// <summary>
    /// Runs the first operation against a database that is down. Its success is what opens the
    /// gate. Null if it failed, which also schedules the next attempt.
    /// </summary>
    public async Task<T?> TryOpenAsync<T>(string doing, Func<Task<T>> work)
        where T : class
    {
        try
        {
            var value = await work().ConfigureAwait(false);
            IsReady = true;
            _lastError = string.Empty;
            logger.LogInformation("database ready");
            return value;
        }
        catch (Exception error) when (IsDatabaseFailure(error))
        {
            Close(doing, error);
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> if the database is up. Null if it is down or the work failed.
    /// </summary>
    public async Task<T?> TryAsync<T>(string doing, Func<Task<T>> work)
        where T : class
    {
        if (!IsReady)
        {
            return null;
        }

        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (PostgresException error)
        {
            // The server answered, so the connection is fine: this statement failed, the next
            // one may not. Closing the gate would stop recording history over one bad row.
            Report(doing, error);
            return null;
        }
        catch (Exception error) when (IsDatabaseFailure(error))
        {
            Close(doing, error);
            return null;
        }
    }

    /// <summary>As <see cref="TryAsync{T}"/>, for work with no result. False if it did not run or failed.</summary>
    public async Task<bool> TryAsync(string doing, Func<Task> work) =>
        await TryAsync(
            doing,
            async () =>
            {
                await work().ConfigureAwait(false);
                return string.Empty;
            }).ConfigureAwait(false) is not null;

    /// <summary>
    /// What the database layer throws when it, rather than our code, failed. Anything else is a
    /// bug and is allowed to surface.
    /// </summary>
    private static bool IsDatabaseFailure(Exception error) =>
        error is DbException or TimeoutException or IOException;

    private void Close(string doing, Exception error)
    {
        IsReady = false;
        _nextAttempt = time.GetTimestamp() + (long)(RetryAfter.TotalSeconds * time.TimestampFrequency);
        Report(doing, error);
    }

    /// <summary>
    /// Once per distinct error, not once per attempt. Npgsql's messages name the host but never
    /// the password, and the connection string itself is never logged (docs/SECURITY.md §3).
    /// </summary>
    private void Report(string doing, Exception error)
    {
        if (error.Message != _lastError)
        {
            logger.LogError("database error while {Doing}: {Error}", doing, error.Message);
            _lastError = error.Message;
        }
    }
}
