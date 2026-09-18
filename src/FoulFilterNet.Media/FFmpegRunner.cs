using System.Diagnostics;

namespace FoulFilterNet.Media;

/// <summary>What an FFmpeg or FFprobe invocation produced.</summary>
public sealed record FFmpegResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// An FFmpeg or FFprobe invocation that did not succeed. The Python this
/// replaces ran every command with <c>check=True</c>, so a non-zero exit is an
/// exception here too — but one that carries FFmpeg's own diagnosis rather than
/// just a number.
/// </summary>
public sealed class FFmpegException : Exception
{
    public FFmpegException() { }

    public FFmpegException(string message)
        : base(message) { }

    public FFmpegException(string message, Exception innerException)
        : base(message, innerException) { }

    public FFmpegException(
        string message,
        int exitCode,
        string standardError,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>The process exit code, or -1 when the process never started.</summary>
    public int ExitCode { get; } = -1;

    /// <summary>Everything the process wrote to standard error.</summary>
    public string StandardError { get; } = string.Empty;
}

/// <summary>
/// Runs FFmpeg and FFprobe. The single seam between this assembly's pure string
/// construction and the outside world — substitute it and nothing downstream
/// starts a process.
/// </summary>
public interface IFFmpegRunner
{
    /// <summary>Run FFmpeg. Throws <see cref="FFmpegException"/> on a non-zero exit.</summary>
    Task<FFmpegResult> RunFFmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    );

    /// <summary>Run FFprobe. Throws <see cref="FFmpegException"/> on a non-zero exit.</summary>
    Task<FFmpegResult> RunFFprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// <see cref="IFFmpegRunner"/> over <see cref="IProcessRunner"/>: the general
/// runner starts the process, and this adds FFmpeg's policy that anything but a
/// clean exit is an <see cref="FFmpegException"/>.
/// </summary>
public sealed class FFmpegRunner : IFFmpegRunner
{
    private readonly FFmpegOptions _options;
    private readonly IProcessRunner _processes;

    public FFmpegRunner()
        : this(new FFmpegOptions()) { }

    public FFmpegRunner(FFmpegOptions options)
        : this(options, new ProcessRunner()) { }

    public FFmpegRunner(FFmpegOptions options, IProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processes);
        _options = options;
        _processes = processes;
    }

    public Task<FFmpegResult> RunFFmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    ) => RunAsync(_options.FFmpegPath, arguments, cancellationToken);

    public Task<FFmpegResult> RunFFprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    ) => RunAsync(_options.FFprobePath, arguments, cancellationToken);

    private async Task<FFmpegResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var description = FFmpegProcess.Describe(fileName, arguments);

        ProcessResult result;
        try
        {
            result = await _processes
                .RunAsync(fileName, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessNotStartedException exception)
        {
            throw new FFmpegException(
                $"Could not start '{fileName}'. Is FFmpeg installed and on PATH? Command was: {description}",
                -1,
                string.Empty,
                exception.InnerException ?? exception
            );
        }

        if (result.ExitCode != 0)
        {
            throw new FFmpegException(
                $"{description} exited with code {result.ExitCode}.{Environment.NewLine}{result.StandardError.Trim()}",
                result.ExitCode,
                result.StandardError
            );
        }

        return new FFmpegResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
