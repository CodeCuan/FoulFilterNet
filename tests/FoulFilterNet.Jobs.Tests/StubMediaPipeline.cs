using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Jobs.Tests;

/// <summary>
/// Stands in for the real pipeline (T21), which Stream E deliberately does not
/// depend on. Every behaviour a job can exhibit - completing, reporting
/// progress, blocking, observing cancellation, throwing - is expressed by
/// setting <see cref="Behaviour"/>.
/// </summary>
internal sealed class StubMediaPipeline : IMediaPipeline
{
    private int _running;

    public Func<JobRequest, IProgress<JobProgress>?, CancellationToken, Task<JobSummary>> Behaviour { get; set; } =
        static (_, _, _) => Task.FromResult(Completed());

    /// <summary>Every request the worker handed over, in order.</summary>
    public List<JobRequest> Requests { get; } = [];

    /// <summary>The highest number of calls that were ever in flight at once.</summary>
    public int PeakConcurrency { get; private set; }

    public static JobSummary Completed(int hits = 0, bool rescanned = false) =>
        new(
            Enumerable.Range(0, hits).Select(i => new Hit("damn", i, i + 0.5)).ToArray(),
            TranscriptWordCount: 10,
            UsedCachedTranscript: false,
            Rescanned: rescanned);

    public async Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var inFlight = Interlocked.Increment(ref _running);
        lock (Requests)
        {
            Requests.Add(request);
            PeakConcurrency = Math.Max(PeakConcurrency, inFlight);
        }

        try
        {
            return await Behaviour(request, progress, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
