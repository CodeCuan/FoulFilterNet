using System.Globalization;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Media;

/// <summary>
/// <see cref="IAudioPreparer"/> over FFmpeg: the three derived-audio renders the
/// analysis stages depend on.
/// </summary>
/// <remarks>
/// Extraction writes AAC because the extracted track is only ever fed back to
/// FFmpeg, while padding and cropping write mono 16 kHz <c>pcm_s16le</c> WAV —
/// the shape Whisper wants, so no decoder runs between here and inference.
/// <para>
/// One deliberate departure from the Python: its <c>crop_audio</c> wrote the
/// span next to the source file, which fails whenever the source lives in a
/// read-only upload directory. Both temporary renders land in
/// <see cref="Path.GetTempPath"/> (or an injected directory) under a unique
/// name instead, so concurrent jobs over the same file cannot collide.
/// </para>
/// </remarks>
public sealed class FFmpegAudioPreparer : IAudioPreparer
{
    /// <summary>
    /// Seconds of silence the Rescan Pass prepends. Mirrors
    /// <c>RESCAN_OFFSET_S</c>. Duplicated rather than shared because
    /// <c>FoulFilterNet.Transcription</c> owns the arithmetic that consumes it
    /// and this assembly may not reference it.
    /// </summary>
    public const double DefaultRescanOffsetSeconds = 4.0;

    /// <summary>Sample rate every derived analysis WAV is resampled to.</summary>
    private const string AnalysisSampleRate = "16000";

    private readonly IFFmpegRunner _runner;
    private readonly string _temporaryDirectory;

    public FFmpegAudioPreparer(IFFmpegRunner runner)
        : this(runner, Path.GetTempPath()) { }

    public FFmpegAudioPreparer(IFFmpegRunner runner, string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        _runner = runner;
        _temporaryDirectory = temporaryDirectory;
    }

    /// <summary>Arguments for the extraction, kept pure so they can be asserted on their own.</summary>
    public static IReadOnlyList<string> BuildExtractArguments(
        string videoPath,
        string outputPath
    ) => FFmpegArguments.Quiet("-i", videoPath, "-vn", "-acodec", "aac", outputPath);

    /// <summary>Arguments for the Rescan Pass's leading silence.</summary>
    public static IReadOnlyList<string> BuildPadArguments(
        string audioPath,
        double offsetSeconds,
        string outputPath
    )
    {
        // Python's int(offset * 1000): whole milliseconds, truncated.
        var delay = ((int)(offsetSeconds * 1000)).ToString(CultureInfo.InvariantCulture);
        return FFmpegArguments.Quiet([
            "-i",
            audioPath,
            "-af",
            $"adelay={delay}|{delay}",
            .. AnalysisWavFormat,
            outputPath,
        ]);
    }

    /// <summary>
    /// Arguments for a cropped span. <c>-ss</c> and <c>-t</c> precede <c>-i</c>
    /// so FFmpeg seeks the container rather than decoding everything before the
    /// span and discarding it.
    /// </summary>
    public static IReadOnlyList<string> BuildCropArguments(
        string audioPath,
        double offsetSeconds,
        double durationSeconds,
        string outputPath
    ) =>
        FFmpegArguments.Quiet([
            "-ss",
            Times.ToFixed(offsetSeconds),
            "-t",
            Times.ToFixed(durationSeconds),
            "-i",
            audioPath,
            .. AnalysisWavFormat,
            outputPath,
        ]);

    public async Task ExtractAudioTrackAsync(
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await _runner
            .RunFFmpegAsync(BuildExtractArguments(videoPath, outputPath), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> PadStartAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetSeconds);

        return RenderTemporaryAsync(
            "rescan",
            outputPath => BuildPadArguments(audioPath, offsetSeconds, outputPath),
            cancellationToken
        );
    }

    public Task<string> CropAsync(
        string audioPath,
        double offsetSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        return RenderTemporaryAsync(
            "span",
            outputPath => BuildCropArguments(audioPath, offsetSeconds, durationSeconds, outputPath),
            cancellationToken
        );
    }

    /// <summary>Mono, 16 kHz, uncompressed, with any picture stream dropped.</summary>
    private static string[] AnalysisWavFormat =>
        ["-vn", "-ac", "1", "-ar", AnalysisSampleRate, "-acodec", "pcm_s16le"];

    /// <summary>
    /// Render to a fresh temporary file and hand its path to the caller, who
    /// owns it from here. A failed render is cleaned up rather than handed back:
    /// the caller asked for audio, not for a truncated file to dispose of.
    /// </summary>
    private async Task<string> RenderTemporaryAsync(
        string tag,
        Func<string, IReadOnlyList<string>> buildArguments,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var outputPath = Path.Combine(_temporaryDirectory, $"ffn_{tag}_{Guid.NewGuid():N}.wav");

        try
        {
            await _runner
                .RunFFmpegAsync(buildArguments(outputPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }

        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temp file outliving a failure is untidy, not fatal.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
