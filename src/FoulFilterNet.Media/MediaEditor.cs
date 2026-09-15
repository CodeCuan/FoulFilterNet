using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Media;

/// <summary>
/// <see cref="IMediaEditor"/> composed from <see cref="FilterGraph"/> and
/// <see cref="IFFmpegRunner"/>: the only place a Censor Method becomes a command.
/// </summary>
/// <remarks>
/// The graphs publish two different labels — silence is wrapped as
/// <c>[aout]</c>, bleep and remove end in <c>[out]</c> — so each render maps the
/// one its own graph produced. Video never re-encodes its picture: the audio is
/// filtered and <c>-c:v copy</c> passes the frames through.
/// <para>
/// One departure from the Python: its bleep path probed the sample rate and
/// discarded the answer, the graph normalising to 44.1 kHz internally. The probe
/// is gone; only <see cref="CensorMethod.Remove"/> still needs a duration, to
/// decide whether a trailing span is worth keeping.
/// </para>
/// </remarks>
public sealed class MediaEditor : IMediaEditor
{
    /// <summary>Suffix a censored render carries when the caller names no output.</summary>
    public const string CensoredSuffix = "_CENSORED";

    /// <summary>
    /// Extension of the track rendered before a bleeped video is muxed back
    /// together. Appended whole to the output path, as the Python did, so the
    /// temporary file cannot collide with a real one.
    /// </summary>
    private const string BleepTrackSuffix = ".bleep_track.m4a";

    private readonly IFFmpegRunner _runner;
    private readonly IMediaProber _prober;

    public MediaEditor(IFFmpegRunner runner, IMediaProber prober)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(prober);
        _runner = runner;
        _prober = prober;
    }

    /// <summary>
    /// Where a censored render lands when the caller names nothing:
    /// <c>&lt;base&gt;_CENSORED&lt;ext&gt;</c>, beside the input.
    /// </summary>
    public static string DefaultOutputPath(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var directory = Path.GetDirectoryName(inputPath);
        var fileName = Path.GetFileNameWithoutExtension(inputPath)
                       + CensoredSuffix
                       + Path.GetExtension(inputPath);

        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    /// <summary>
    /// The output path to render to. A blank one means "you choose", which is
    /// how a caller with no preference asks for the default naming.
    /// </summary>
    public static string ResolveOutputPath(string inputPath, string? outputPath) =>
        string.IsNullOrWhiteSpace(outputPath) ? DefaultOutputPath(inputPath) : outputPath;

    public async Task CensorAudioAsync(
        string inputPath,
        IReadOnlyList<Hit> hits,
        CensorMethod method,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(hits);

        var destination = PrepareDestination(inputPath, outputPath);

        var graph = method switch
        {
            CensorMethod.Remove => FilterGraph.Remove(hits, await KnownDurationAsync(inputPath, cancellationToken).ConfigureAwait(false)),
            CensorMethod.Bleep => FilterGraph.Bleep(hits),
            _ => FilterGraph.LabelSilence(FilterGraph.Silence(hits)),
        };

        var label = method is CensorMethod.Silence ? FilterGraph.SilenceOutputLabel : FilterGraph.OutputLabel;

        await RunAsync(
            ["-i", inputPath, "-filter_complex", graph, "-map", label, destination],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CensorVideoAsync(
        string inputPath,
        IReadOnlyList<Hit> hits,
        CensorMethod method,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(hits);

        var destination = PrepareDestination(inputPath, outputPath);

        if (method is CensorMethod.Bleep)
        {
            await BleepAsync(inputPath, hits, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Remove falls back to Silence here (ADR-0004): cutting the audio would
        // slide it out of step with a picture that is being copied verbatim.
        await RunAsync(
            [
                "-i", inputPath,
                "-filter_complex", FilterGraph.LabelSilence(FilterGraph.Silence(hits)),
                "-map", "0:v", "-map", FilterGraph.SilenceOutputLabel,
                "-c:v", "copy",
                destination,
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A bleep mixes generated sine sources, which cannot be done in the same
    /// pass that stream-copies the picture. Render the censored track, mux it
    /// back over the original frames, and clean the track up whatever happens.
    /// </summary>
    private async Task BleepAsync(
        string inputPath,
        IReadOnlyList<Hit> hits,
        string destination,
        CancellationToken cancellationToken)
    {
        var track = destination + BleepTrackSuffix;

        try
        {
            await RunAsync(
                [
                    "-i", inputPath, "-vn",
                    "-filter_complex", FilterGraph.Bleep(hits),
                    "-map", FilterGraph.OutputLabel,
                    track,
                ],
                cancellationToken).ConfigureAwait(false);

            await RunAsync(
                [
                    "-i", inputPath,
                    "-i", track,
                    "-map", "0:v", "-map", "1:a",
                    "-c:v", "copy",
                    destination,
                ],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(track);
        }
    }

    private Task<FFmpegResult> RunAsync(string[] arguments, CancellationToken cancellationToken) =>
        _runner.RunFFmpegAsync(FFmpegArguments.Quiet(arguments), cancellationToken);

    /// <summary>
    /// The file's length, or <see langword="null"/> when FFprobe could not tell.
    /// Zero is its "I do not know", and passing that on as a real length would
    /// make the remove graph drop the tail and then refuse to build.
    /// </summary>
    private async Task<double?> KnownDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var info = await _prober.ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
        return info.DurationSeconds > 0 ? info.DurationSeconds : null;
    }

    private static string PrepareDestination(string inputPath, string? outputPath)
    {
        var destination = ResolveOutputPath(inputPath, outputPath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return destination;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // An orphaned intermediate track is untidy, not fatal, and must
            // never mask the failure that left it behind.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
