using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Web;

/// <summary>
/// Stands in for the orchestrator until T21 lands, so the host can start and the
/// queue, the endpoints and the event stream can all be exercised end to end
/// without it. A job that reaches this fails with an explanation rather than
/// hanging, which is exactly how the worker reports any other pipeline failure.
/// <para>T21 replaces the registration in <c>Program.cs</c>; nothing else.</para>
/// </summary>
internal sealed class PendingMediaPipeline : IMediaPipeline
{
    public Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The media pipeline is not wired up in this build.");
}
