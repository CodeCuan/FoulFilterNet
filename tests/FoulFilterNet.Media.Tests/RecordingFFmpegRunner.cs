namespace FoulFilterNet.Media.Tests;

/// <summary>
/// An <see cref="IFFmpegRunner"/> that starts nothing and remembers every
/// invocation in order. The point of the runner seam is that argument
/// construction can be asserted without FFmpeg present; a recording fake also
/// makes the *sequence* of invocations assertable, which matters for the
/// two-step bleep-on-video path.
/// </summary>
internal sealed class RecordingFFmpegRunner : IFFmpegRunner
{
    private readonly List<IReadOnlyList<string>> _ffmpegCalls = [];
    private readonly List<IReadOnlyList<string>> _ffprobeCalls = [];

    /// <summary>Every FFmpeg invocation, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<string>> FFmpegCalls => _ffmpegCalls;

    /// <summary>Every FFprobe invocation, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<string>> FFprobeCalls => _ffprobeCalls;

    /// <summary>The most recent FFmpeg invocation.</summary>
    public IReadOnlyList<string> LastFFmpegCall => _ffmpegCalls[^1];

    /// <summary>What <see cref="RunFFprobeAsync"/> hands back as standard output.</summary>
    public string FFprobeOutput { get; set; } = string.Empty;

    /// <summary>
    /// Runs for each FFmpeg invocation after it has been recorded. Create files,
    /// or throw, to stand in for what the real process would have done.
    /// </summary>
    public Action<IReadOnlyList<string>>? OnFFmpeg { get; set; }

    public Task<FFmpegResult> RunFFmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    )
    {
        _ffmpegCalls.Add([.. arguments]);
        OnFFmpeg?.Invoke(arguments);
        return Task.FromResult(new FFmpegResult(0, string.Empty, string.Empty));
    }

    public Task<FFmpegResult> RunFFprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    )
    {
        _ffprobeCalls.Add([.. arguments]);
        return Task.FromResult(new FFmpegResult(0, FFprobeOutput, string.Empty));
    }
}

/// <summary>
/// A directory that deletes itself, so tests that let the production code choose
/// its own temporary file names still leave nothing behind.
/// </summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ffn_tests_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

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
            // A leftover scratch directory is not worth failing a test over.
        }
    }
}
