namespace FoulFilterNet.Domain.Abstractions;

/// <summary>
/// Inspects a media file. Backed by FFprobe rather than libmagic: FFmpeg is
/// already a hard dependency and can answer "does this have a video stream?"
/// authoritatively.
/// </summary>
public interface IMediaProber
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Produces the derived audio the analysis stages work from.</summary>
public interface IAudioPreparer
{
    /// <summary>Extract a video's audio track so it can be transcribed.</summary>
    Task ExtractAudioTrackAsync(string videoPath, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepend silence, shifting every chunk boundary for the Rescan Pass.
    /// Returns the path of a temporary file the caller owns and must delete.
    /// </summary>
    Task<string> PadStartAsync(string audioPath, double offsetSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract a mono 16 kHz span. Returns the path of a temporary file the
    /// caller owns and must delete.
    /// </summary>
    Task<string> CropAsync(string audioPath, double offsetSeconds, double durationSeconds, CancellationToken cancellationToken = default);
}

/// <summary>Renders the Censor Method onto a media file.</summary>
public interface IMediaEditor
{
    Task CensorAudioAsync(
        string inputPath,
        IReadOnlyList<Hit> hits,
        CensorMethod method,
        string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Censor a video's audio while stream-copying its frames. Implementations
    /// fall back to <see cref="CensorMethod.Silence"/> when asked for
    /// <see cref="CensorMethod.Remove"/>, since cutting would desynchronise the
    /// picture.
    /// </summary>
    Task CensorVideoAsync(
        string inputPath,
        IReadOnlyList<Hit> hits,
        CensorMethod method,
        string outputPath,
        CancellationToken cancellationToken = default);
}
