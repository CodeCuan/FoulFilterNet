using FoulFilterNet.Sources;

namespace FoulFilterNet.Web;

/// <summary>
/// Whether web video can work on this machine, as <c>GET /config</c> reports
/// it: <see cref="IWebAudioSource.CheckAvailabilityAsync"/>, run once and kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why cache it.</b> The probe starts yt-dlp and Deno with
/// <c>--version</c>. yt-dlp on Windows is a self-extracting bundle that takes
/// over a second to start (W01), and the UI asks for <c>/config</c> on every
/// page load. Probing each time would make the batch UI slower to appear for
/// the sake of a feature it does not use.
/// </para>
/// <para>
/// <b>How long.</b> A working answer is kept for <see cref="AvailableFor"/>,
/// so an uninstall is noticed within minutes. A missing tool is re-checked
/// after <see cref="UnavailableFor"/>: the user who has just installed Deno
/// reloads the page expecting it to be believed. Age runs from when the
/// probe <em>finished</em>; a probe still running is never stale.
/// </para>
/// <para>
/// <b>One probe for everyone.</b> Callers arriving while a probe runs wait for
/// that probe. The probe itself runs on the host's shutdown token, not a
/// caller's: one request hanging up must not cancel the answer the others are
/// waiting for, and it is the host stopping that should kill a yt-dlp still
/// running.
/// </para>
/// <para>
/// <b>Started with the host.</b> As a hosted service it begins the first probe
/// at startup without waiting for it, so the UI's first <c>/config</c> usually
/// finds the answer ready.
/// </para>
/// <para>
/// <b>Never fails <c>/config</c>.</b> The source reports a missing tool as a
/// null version rather than throwing; anything it does throw is logged and read
/// as "neither found", and retried like any unavailable answer.
/// </para>
/// </remarks>
public sealed partial class WebVideoAvailabilityCache(
    IWebAudioSource source,
    TimeProvider time,
    ILogger<WebVideoAvailabilityCache>? logger = null
) : IHostedService, IDisposable
{
    /// <summary>How long an answer that says web video works is kept.</summary>
    public static readonly TimeSpan AvailableFor = TimeSpan.FromMinutes(5);

    /// <summary>How long an answer that says a tool is missing is kept.</summary>
    public static readonly TimeSpan UnavailableFor = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private int _stopped;
    private Task<WebVideoAvailability>? _probe;
    private DateTimeOffset _answeredAt;

    /// <summary>
    /// The current answer: a kept one if it is fresh, else the probe already
    /// running, else a new probe.
    /// </summary>
    /// <param name="cancellationToken">Stops this caller waiting; the probe carries on for others.</param>
    /// <exception cref="OperationCanceledException">This caller gave up, or the host is stopping.</exception>
    public Task<WebVideoAvailability> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<WebVideoAvailability> probe;
        lock (_gate)
        {
            if (_probe is null || IsStale(_probe))
            {
                _probe = ProbeAsync(_stopping.Token);
            }

            probe = _probe;
        }

        return probe.WaitAsync(cancellationToken);
    }

    /// <summary>Starts the first probe and returns without waiting for it.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = GetAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Cancels a probe still running, which kills its processes.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels a probe still running. The token source itself is left for the
    /// collector: under minimal hosting the container can dispose this
    /// singleton before the host calls <see cref="StopAsync"/>, and the source
    /// holds no timer or handle worth releasing early.
    /// </summary>
    public void Dispose() => Stop();

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stopping.Cancel();
        }
    }

    /// <summary>Called under the lock.</summary>
    private bool IsStale(Task<WebVideoAvailability> probe)
    {
        if (!probe.IsCompleted)
        {
            return false;
        }

        // Cancelled by shutdown: nothing to keep, and asking again is harmless.
        if (!probe.IsCompletedSuccessfully)
        {
            return true;
        }

        var lifetime = probe.Result.IsAvailable ? AvailableFor : UnavailableFor;
        return time.GetUtcNow() - _answeredAt >= lifetime;
    }

    private async Task<WebVideoAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        WebVideoAvailability answer;
        try
        {
            answer = await source.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger is not null)
            {
                LogProbeFailed(logger, exception);
            }

            answer = new WebVideoAvailability(null, null);
        }

        lock (_gate)
        {
            _answeredAt = time.GetUtcNow();
        }

        return answer;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not check whether yt-dlp and Deno are installed; reporting web video as unavailable"
    )]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);
}
