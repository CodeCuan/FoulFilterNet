using System.ComponentModel;
using System.Diagnostics;

namespace FoulFilterNet.Media;

/// <summary>What a finished process produced.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything written to standard output.</param>
/// <param name="StandardError">Everything written to standard error.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// The executable could not be started at all - almost always because it is not
/// installed or not on <c>PATH</c>. Distinct from a process that started and
/// failed, which is a <see cref="ProcessResult"/> with a non-zero exit code.
/// </summary>
public sealed class ProcessNotStartedException : Exception
{
    public ProcessNotStartedException() { }

    public ProcessNotStartedException(string message)
        : base(message) { }

    public ProcessNotStartedException(string message, Exception innerException)
        : base(message, innerException) { }

    public ProcessNotStartedException(string fileName, Win32Exception innerException)
        : base($"Could not start '{fileName}': {innerException?.Message}", innerException)
    {
        FileName = fileName;
    }

    /// <summary>The executable that could not be started.</summary>
    public string FileName { get; } = string.Empty;
}

/// <summary>
/// Runs an external program to completion. The general seam under
/// <see cref="FFmpegRunner"/> and the yt-dlp audio source: substitute it and
/// nothing starts a process.
/// </summary>
/// <remarks>
/// It deliberately does not judge the exit code. FFmpeg's callers want any
/// non-zero exit to be an exception, which <see cref="FFmpegRunner"/> adds on
/// top; yt-dlp exits 1 for a private video and for a dropped connection alike,
/// so its caller needs standard error back either way to tell them apart.
/// </remarks>
public interface IProcessRunner
{
    /// <summary>
    /// Start <paramref name="fileName"/> with <paramref name="arguments"/> and
    /// wait for it to exit.
    /// </summary>
    /// <exception cref="ProcessNotStartedException">The executable could not be started.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process tree has been killed.</exception>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    );
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long to wait for a killed process to be gone before giving up on it.
    /// Callers that delete the files it was writing need it dead first, and on
    /// Windows a file stays locked until the handle closes.
    /// </summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

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
            throw new ProcessNotStartedException(fileName, exception);
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
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// Kill the whole tree - yt-dlp's PyInstaller executable is a bootloader
    /// with a child, and both may have FFmpeg under them - and give it a moment
    /// to be gone.
    /// </summary>
    private static async Task KillAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);

            using var grace = new CancellationTokenSource(KillGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Nothing useful to do if the kill itself fails.
        }
        catch (OperationCanceledException)
        {
            // It outlived the grace period; the caller's cancellation still wins.
        }
    }
}
