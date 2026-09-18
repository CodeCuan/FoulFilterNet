using FoulFilterNet.Media;

namespace FoulFilterNet.Sources;

/// <summary>
/// <see cref="IWebAudioSource"/> over yt-dlp: one process per fetch, run through
/// the same <see cref="IProcessRunner"/> seam FFmpeg uses, so every outcome can
/// be driven from captured output without yt-dlp or a network.
/// </summary>
/// <remarks>
/// <para>
/// A clean exit is necessary, not sufficient. The audio is trusted only when
/// yt-dlp printed both marked lines, the file it names is directly inside the
/// directory we asked for, and the file exists. A livestream skipped by the
/// match filter also exits 0, with the info line and no file line, and becomes
/// <see cref="WebAudioFailure.Unsupported"/>.
/// </para>
/// <para>
/// Whatever does not succeed leaves no <c>audio.*</c> behind: a failure or a
/// cancellation deletes the partial download (<c>.part</c>, <c>.ytdl</c>), after
/// the runner has killed the process tree. Files that are not the source's own
/// are never touched, including one yt-dlp claimed to have written elsewhere.
/// </para>
/// </remarks>
public sealed class YtDlpAudioSource : IWebAudioSource
{
    /// <summary>
    /// How long a version check may take before the tool counts as missing. The
    /// probe backs <c>GET /config</c>, which must not hang on a wedged
    /// executable; yt-dlp takes about 1.2 s to start (W01).
    /// </summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(20);

    private const int DeleteAttempts = 10;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IProcessRunner _runner;
    private readonly SourcesOptions _options;
    private readonly TimeSpan _probeTimeout;

    public YtDlpAudioSource(IProcessRunner runner, SourcesOptions options)
        : this(runner, options, DefaultProbeTimeout) { }

    public YtDlpAudioSource(IProcessRunner runner, SourcesOptions options, TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.YtDlpPath, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);

        _runner = runner;
        _options = options;
        _probeTimeout = probeTimeout;
    }

    public async Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Directory.CreateDirectory(fullDirectory);
        await DeleteOwnFilesAsync(fullDirectory).ConfigureAwait(false);

        var arguments = YtDlpArguments.ForFetch(video, fullDirectory, _options.DenoPath);

        ProcessResult result;
        try
        {
            result = await _runner
                .RunAsync(_options.YtDlpPath, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessNotStartedException exception)
        {
            var reason =
                $"yt-dlp could not be started from '{_options.YtDlpPath}'. Install it and put it on PATH, or set Sources:YtDlpPath.";
            throw new WebAudioException(
                video,
                WebAudioFailure.NotInstalled,
                reason,
                Describe(video, reason),
                innerException: exception
            );
        }
        catch (OperationCanceledException)
        {
            await DeleteOwnFilesAsync(fullDirectory).ConfigureAwait(false);
            throw;
        }

        try
        {
            return Interpret(video, fullDirectory, result);
        }
        catch (WebAudioException)
        {
            await DeleteOwnFilesAsync(fullDirectory).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ytDlp = ProbeAsync(
            _options.YtDlpPath,
            YtDlpArguments.ForVersion(),
            YtDlpDiagnostics.YtDlpVersion,
            cancellationToken
        );
        var deno = ProbeAsync(
            string.IsNullOrWhiteSpace(_options.DenoPath) ? "deno" : _options.DenoPath,
            YtDlpArguments.ForDenoVersion(),
            YtDlpDiagnostics.DenoVersion,
            cancellationToken
        );

        return new WebVideoAvailability(
            await ytDlp.ConfigureAwait(false),
            await deno.ConfigureAwait(false)
        );
    }

    /// <summary>Turn a finished run into the audio, or the reason there is none.</summary>
    private static WebAudio Interpret(VideoRef video, string directory, ProcessResult result)
    {
        var jsRuntimeMissing = YtDlpDiagnostics.ReportsMissingJsRuntime(result.StandardError);

        if (result.ExitCode != 0)
        {
            var kind = YtDlpDiagnostics.Classify(result.StandardError);
            if (kind == WebAudioFailure.Failed && jsRuntimeMissing)
            {
                kind = WebAudioFailure.JsRuntimeMissing;
            }

            var reason =
                YtDlpDiagnostics.Reason(result.StandardError)
                ?? $"yt-dlp failed with exit code {result.ExitCode} and said nothing.";

            throw Failure(video, kind, reason, result, Advice(kind, result.ExitCode));
        }

        var output = YtDlpOutput.Parse(result.StandardOutput);

        if (output.Info is { IsLive: true } live && output.File is null)
        {
            var reason =
                live.LiveStatus == "is_upcoming"
                    ? "This is an upcoming livestream or premiere, which cannot be filtered."
                    : "This is a livestream, which cannot be filtered.";
            throw Failure(video, WebAudioFailure.Unsupported, reason, result);
        }

        if (output.Info is null || output.File is null)
        {
            throw Failure(
                video,
                WebAudioFailure.Failed,
                "yt-dlp exited cleanly but did not report the downloaded audio.",
                result
            );
        }

        var path = Path.GetFullPath(output.File.FilePath);

        if (!IsDirectlyInside(path, directory))
        {
            throw Failure(
                video,
                WebAudioFailure.Failed,
                $"yt-dlp reported the audio outside the directory it was given: '{path}'.",
                result
            );
        }

        if (!File.Exists(path))
        {
            throw Failure(
                video,
                WebAudioFailure.Failed,
                $"yt-dlp reported '{path}', but there is no such file.",
                result
            );
        }

        return new WebAudio(
            video,
            output.Info.Title,
            output.Info.DurationSeconds,
            path,
            output.File.Extension,
            output.File.FormatId,
            output.File.Codec,
            jsRuntimeMissing
        );
    }

    private static WebAudioException Failure(
        VideoRef video,
        WebAudioFailure kind,
        string reason,
        ProcessResult result,
        string? advice = null
    ) =>
        new(
            video,
            kind,
            reason,
            Describe(video, reason, advice),
            result.ExitCode,
            result.StandardError
        );

    private static string Describe(VideoRef video, string reason, string? advice = null) =>
        advice is null
            ? $"Could not fetch the audio of {video.Key}: {reason}"
            : $"Could not fetch the audio of {video.Key}: {reason} {advice}";

    /// <summary>What the user can do about it, where there is something.</summary>
    private static string? Advice(WebAudioFailure kind, int exitCode) =>
        kind switch
        {
            WebAudioFailure.JsRuntimeMissing =>
                "yt-dlp found no JavaScript runtime; install Deno and put it on PATH, or set Sources:DenoPath.",
            WebAudioFailure.Failed when exitCode == 2 =>
                "yt-dlp rejected its arguments; it may be too old - update it (yt-dlp -U).",
            WebAudioFailure.Failed =>
                "If this persists, update yt-dlp (yt-dlp -U): YouTube changes often.",
            _ => null,
        };

    private static bool IsDirectlyInside(string path, string directory) =>
        string.Equals(
            Path.GetDirectoryName(path),
            directory,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    /// <summary>
    /// Delete the <c>audio.*</c> files in <paramref name="directory"/>. On Windows a killed process's handles can
    /// outlive it by a moment, so a locked file is retried briefly and then left.
    /// </summary>
    private static async Task DeleteOwnFilesAsync(string directory)
    {
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            var remaining = false;

            foreach (
                var file in Directory.EnumerateFiles(directory, YtDlpArguments.AudioFileStem + ".*")
            )
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    remaining = true;
                }
            }

            if (!remaining)
            {
                return;
            }

            await Task.Delay(DeleteRetryDelay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run a version check. A tool that cannot start, exits non-zero, prints no
    /// recognisable version or outlives the probe timeout counts as missing;
    /// only the caller's own cancellation escapes.
    /// </summary>
    private async Task<string?> ProbeAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Func<string, string?> readVersion,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);

        try
        {
            var result = await _runner
                .RunAsync(fileName, arguments, timeout.Token)
                .ConfigureAwait(false);
            return result.ExitCode == 0 ? readVersion(result.StandardOutput) : null;
        }
        catch (ProcessNotStartedException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
