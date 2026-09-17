using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using FoulFilterNet.Jobs;
using FoulFilterNet.Web.Contracts;

namespace FoulFilterNet.Web.Endpoints;

/// <summary>
/// The live job feed the batch UI renders from.
/// </summary>
public static class EventEndpoints
{
    /// <summary>
    /// How long a disconnected client waits before reconnecting, matching the
    /// Python's <c>retry: 3000</c>.
    /// </summary>
    private static readonly TimeSpan ReconnectAfter = TimeSpan.FromSeconds(3);

    public static void MapEventEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(
            "/events",
            (
                JobManager jobs,
                IHostApplicationLifetime lifetime,
                CancellationToken cancellationToken
            ) =>
                TypedResults.ServerSentEvents(
                    StreamAsync(jobs, lifetime.ApplicationStopping, cancellationToken)
                )
        );
    }

    /// <summary>
    /// Every job as it stands right now, then every change as it happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot is replayed before the updates because
    /// <see cref="JobManager.Subscribe"/> takes it under the same lock that
    /// publishes, so a change landing between connecting and reading cannot slip
    /// through the gap. The Python fetched <c>/jobs</c> and subscribed
    /// separately, leaving exactly that race open.
    /// </para>
    /// <para>
    /// The stream ends when the client goes away <em>or</em> when the host
    /// starts shutting down. Watching only the request's token is not enough: an
    /// idle listener would keep an open response, and therefore shutdown itself,
    /// waiting indefinitely for an event that is never coming.
    /// </para>
    /// <para>
    /// There is deliberately no periodic keepalive. The Python sent
    /// <c>: keepalive</c> comments every 15 seconds to stop an idle connection
    /// being dropped by an intermediary, and SSE comments are not expressible
    /// through <see cref="SseItem{T}"/>; faking them as empty events would push
    /// unparseable payloads at the client's <c>onmessage</c>. It is not needed
    /// here: if an idle connection is dropped, EventSource reconnects after
    /// <see cref="ReconnectAfter"/> and the snapshot replay above means the
    /// client misses nothing by having been away.
    /// </para>
    /// </remarks>
    private static async IAsyncEnumerable<SseItem<JobView>> StreamAsync(
        JobManager jobs,
        CancellationToken applicationStopping,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            applicationStopping
        );

        using var subscription = jobs.Subscribe();

        foreach (var record in subscription.Snapshot)
        {
            yield return Event(record);
        }

        var updates = subscription.Updates;
        while (true)
        {
            // Waiting and yielding are separated because a yield cannot sit
            // inside a try/catch, and cancelling the wait is the normal way this
            // loop ends rather than an error.
            bool more;
            try
            {
                more = await updates.WaitToReadAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!more)
            {
                yield break;
            }

            while (updates.TryRead(out var record))
            {
                yield return Event(record);
            }
        }
    }

    /// <summary>
    /// One job, unnamed so the front end's <c>es.onmessage</c> fires for it -
    /// naming the event would leave every row in the table frozen.
    /// </summary>
    /// <remarks>
    /// The reconnection interval rides on every event rather than just the
    /// first, so a client that reconnects part-way through still learns it.
    /// </remarks>
    private static SseItem<JobView> Event(JobRecord record) =>
        new(JobView.From(record)) { ReconnectionInterval = ReconnectAfter };
}
