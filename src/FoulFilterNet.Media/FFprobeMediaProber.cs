using System.Globalization;
using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Media;

/// <summary>
/// Turns FFprobe's JSON into a <see cref="MediaInfo"/>. Pure, so the decision
/// that used to belong to libmagic can be asserted against captured output.
/// </summary>
/// <remarks>
/// The Python this replaces short-circuited <c>.m4b</c> and <c>.m4a</c> by
/// extension, because libmagic reported MPEG-4 audiobooks as video. FFprobe
/// answers from the stream list instead, so the hack is gone: an audiobook has
/// no video stream and is classified as audio on the evidence.
/// <para>
/// One wrinkle libmagic never had to handle: cover art arrives as a video
/// stream. It is marked <c>attached_pic</c> and does not make a file a video.
/// </para>
/// </remarks>
public static class FFprobeReport
{
    private const string AudioCodecType = "audio";
    private const string VideoCodecType = "video";

    /// <summary>
    /// Parse <c>ffprobe -print_format json -show_format -show_streams</c> output.
    /// Anything unparseable is <see cref="MediaKind.Unknown"/> rather than an
    /// exception — an unrecognised upload is a rejection, not a crash.
    /// </summary>
    public static MediaInfo Parse(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Unrecognised;
        }

        var streams =
            root.TryGetProperty("streams", out var streamList)
            && streamList.ValueKind == JsonValueKind.Array
                ? streamList.EnumerateArray().ToList()
                : [];

        var hasPicture = streams.Any(stream =>
            IsCodecType(stream, VideoCodecType) && !IsAttachedPicture(stream)
        );
        var audio = streams.Find(stream => IsCodecType(stream, AudioCodecType));
        var hasAudio = audio.ValueKind == JsonValueKind.Object;

        var kind =
            hasPicture ? MediaKind.Video
            : hasAudio ? MediaKind.Audio
            : MediaKind.Unknown;

        if (kind == MediaKind.Unknown)
        {
            return Unrecognised;
        }

        return new MediaInfo(
            kind,
            ReadDuration(root, streams),
            hasAudio ? ReadSampleRate(audio) : null
        );
    }

    private static MediaInfo Unrecognised => new(MediaKind.Unknown, 0, null);

    private static bool IsCodecType(JsonElement stream, string codecType) =>
        stream.ValueKind == JsonValueKind.Object
        && stream.TryGetProperty("codec_type", out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), codecType, StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty("attached_pic", out var attached)
        && attached.ValueKind == JsonValueKind.Number
        && attached.GetInt32() != 0;

    /// <summary>
    /// The container's duration, falling back to the longest stream's. A live
    /// input reports neither, and zero is the honest answer.
    /// </summary>
    private static double ReadDuration(JsonElement root, List<JsonElement> streams)
    {
        if (
            root.TryGetProperty("format", out var format)
            && TryReadDouble(format, "duration", out var containerDuration)
        )
        {
            return containerDuration;
        }

        var longest = 0.0;
        foreach (var stream in streams)
        {
            if (
                TryReadDouble(stream, "duration", out var streamDuration)
                && streamDuration > longest
            )
            {
                longest = streamDuration;
            }
        }

        return longest;
    }

    private static int? ReadSampleRate(JsonElement audioStream) =>
        audioStream.TryGetProperty("sample_rate", out var value)
        && value.ValueKind == JsonValueKind.String
        && int.TryParse(
            value.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var rate
        )
            ? rate
            : null;

    private static bool TryReadDouble(JsonElement element, string propertyName, out double result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result
            );
    }
}

/// <summary><see cref="IMediaProber"/> backed by FFprobe.</summary>
public sealed class FFprobeMediaProber : IMediaProber
{
    private readonly IFFmpegRunner _runner;

    public FFprobeMediaProber(IFFmpegRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>The argument list, kept pure so it can be asserted on its own.</summary>
    public static IReadOnlyList<string> BuildArguments(string path) =>
        ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path];

    public async Task<MediaInfo> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await _runner
                .RunFFprobeAsync(BuildArguments(path), cancellationToken)
                .ConfigureAwait(false);
            return FFprobeReport.Parse(result.StandardOutput);
        }
        catch (FFmpegException)
        {
            // FFprobe rejects anything it cannot demux. That is the answer to
            // "what kind of media is this?", not a failure worth propagating.
            return new MediaInfo(MediaKind.Unknown, 0, null);
        }
    }
}
