using System.ComponentModel;
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

/// <summary><see cref="IFFmpegRunner"/> over <see cref="Process"/>.</summary>
public sealed class FFmpegRunner : IFFmpegRunner
{
    private readonly FFmpegOptions _options;

    public FFmpegRunner()
        : this(new FFmpegOptions()) { }

    public FFmpegRunner(FFmpegOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public Task<FFmpegResult> RunFFmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    ) => RunAsync(_options.FFmpegPath, arguments, cancellationToken);

    public Task<FFmpegResult> RunFFprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    ) => RunAsync(_options.FFprobePath, arguments, cancellationToken);

    private static async Task<FFmpegResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var description = FFmpegProcess.Describe(fileName, arguments);

        using var process = new Process
        {
            StartInfo = FFmpegProcess.CreateStartInfo(fileName, arguments),
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new FFmpegException(
                $"Could not start '{fileName}'. Is FFmpeg installed and on PATH? Command was: {description}",
                -1,
                string.Empty,
                exception
            );
        }

        // Both streams are drained concurrently: a process that fills one pipe
        // while we block reading the other deadlocks.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new FFmpegException(
                $"{description} exited with code {process.ExitCode}.{Environment.NewLine}{error.Trim()}",
                process.ExitCode,
                error
            );
        }

        return new FFmpegResult(process.ExitCode, output, error);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Nothing useful to do if the kill itself fails.
        }
    }
}
