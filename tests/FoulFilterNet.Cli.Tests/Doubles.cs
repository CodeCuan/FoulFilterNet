using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Cli.Tests;

/// <summary>A prober that answers from memory rather than from FFprobe.</summary>
internal sealed class StubProber : IMediaProber
{
    private readonly Exception? _failure;

    public StubProber(MediaKind kind, Exception? failure = null)
    {
        Kind = kind;
        _failure = failure;
    }

    public MediaKind Kind { get; }

    public List<string> Probed { get; } = [];

    public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        Probed.Add(path);
        return _failure is null
            ? Task.FromResult(new MediaInfo(Kind, 8.0, 16000))
            : Task.FromException<MediaInfo>(_failure);
    }
}

/// <summary>
/// Stands in for <see cref="FoulFilterNet.Pipeline.MediaPipeline"/>. It writes
/// the output file when - and only when - the request asked it to render, which
/// is the behaviour <c>--no_edit</c> has to suppress.
/// </summary>
internal sealed class StubPipeline : IMediaPipeline
{
    private readonly IReadOnlyList<Hit> _hits;
    private readonly Exception? _failure;

    public StubPipeline(IReadOnlyList<Hit>? hits = null, Exception? failure = null)
    {
        _hits = hits ?? [];
        _failure = failure;
    }

    public List<JobRequest> Requests { get; } = [];

    /// <summary>Progress checkpoints the runner will be handed, in order.</summary>
    public List<JobProgress> Checkpoints { get; } = [];

    public Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        foreach (var checkpoint in Checkpoints)
        {
            progress?.Report(checkpoint);
        }

        if (_failure is not null)
        {
            return Task.FromException<JobSummary>(_failure);
        }

        if (request.Render)
        {
            File.WriteAllText(request.OutputPath, "censored");
        }

        return Task.FromResult(new JobSummary(_hits, TranscriptWordCount: 12, UsedCachedTranscript: false, Rescanned: request.Rescan));
    }
}

/// <summary>A directory that deletes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foulfilter-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test run is not a failure.
        }
    }
}
