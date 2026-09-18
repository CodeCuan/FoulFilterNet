using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Watch;

/// <summary>
/// Drops expired Watch Sessions every <see cref="WatchOptions.SweepInterval"/>,
/// which is what cancels the work of a video nobody is watching any more.
/// </summary>
/// <remarks>
/// A sweep is only housekeeping: heartbeats and lookups apply the same expiry
/// rule to the session they touch, so a late sweep never serves a stale
/// session. What it adds is that a tab closed mid-transcription stops using the
/// GPU within <see cref="WatchOptions.IdleTimeout"/> plus one interval, and
/// that retained snapshots are let go. On shutdown the sessions are cancelled
/// by the manager's disposal, not here.
/// </remarks>
public sealed partial class WatchSessionSweeper(
    WatchSessionManager sessions,
    IOptions<WatchOptions> options,
    TimeProvider time,
    ILogger<WatchSessionSweeper> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var dropped = sessions.SweepExpired();
                if (dropped > 0)
                {
                    LogDropped(dropped);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped {Count} expired Watch Session(s)")]
    private partial void LogDropped(int count);
}
