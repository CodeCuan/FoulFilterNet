using Whisper.net;
using Whisper.net.Ggml;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Maps a configured model name onto the things whisper.cpp understands: a GGML
/// weights file name, and the <see cref="GgmlType"/> that names the same weights
/// to Whisper.net's downloader.
/// </summary>
/// <remarks>
/// Configuration keeps the Hugging Face spelling the Python used
/// (<see cref="ModelNames"/>), so <c>base</c> and <c>openai/whisper-base</c>
/// both mean <c>ggml-base.bin</c>. A third-party repository id still names a
/// file - the model half of it - but has no <see cref="GgmlType"/>, so those
/// weights have to be installed by hand rather than fetched.
/// </remarks>
public static class WhisperModelFiles
{
    /// <summary>Prefix every whisper.cpp weights file carries.</summary>
    public const string Prefix = "ggml-";

    /// <summary>Extension every whisper.cpp weights file carries.</summary>
    public const string Extension = ".bin";

    /// <summary>The weights file a model name refers to, e.g. <c>ggml-base.bin</c>.</summary>
    public static string FileName(string model) => Prefix + Size(model) + Extension;

    /// <summary>
    /// The model as Whisper.net's downloader names it, or null when it is not a
    /// model Whisper.net can fetch.
    /// </summary>
    public static GgmlType? GgmlTypeFor(string model) => Size(model) switch
    {
        "tiny" => GgmlType.Tiny,
        "tiny.en" => GgmlType.TinyEn,
        "base" => GgmlType.Base,
        "base.en" => GgmlType.BaseEn,
        "small" => GgmlType.Small,
        "small.en" => GgmlType.SmallEn,
        "medium" => GgmlType.Medium,
        "medium.en" => GgmlType.MediumEn,
        "large-v1" => GgmlType.LargeV1,
        "large-v2" => GgmlType.LargeV2,
        "large-v3" => GgmlType.LargeV3,
        "large-v3-turbo" => GgmlType.LargeV3Turbo,
        _ => null,
    };

    /// <summary>
    /// The attention heads whisper.cpp aligns against to produce DTW word
    /// timestamps, or null for a model it does not know - in which case DTW has
    /// to stay off, since there would be no heads to align with.
    /// </summary>
    /// <remarks>
    /// DTW timestamps are the ones worth having: ADR-0006 measured them at a
    /// third of the boundary error of the plain token-timestamp heuristic.
    /// </remarks>
    public static WhisperAlignmentHeadsPreset? AlignmentHeadsFor(string model) => Size(model) switch
    {
        "tiny" => WhisperAlignmentHeadsPreset.Tiny,
        "tiny.en" => WhisperAlignmentHeadsPreset.TinyEn,
        "base" => WhisperAlignmentHeadsPreset.Base,
        "base.en" => WhisperAlignmentHeadsPreset.BaseEn,
        "small" => WhisperAlignmentHeadsPreset.Small,
        "small.en" => WhisperAlignmentHeadsPreset.SmallEn,
        "medium" => WhisperAlignmentHeadsPreset.Medium,
        "medium.en" => WhisperAlignmentHeadsPreset.MediumEn,
        "large-v1" => WhisperAlignmentHeadsPreset.LargeV1,
        "large-v2" => WhisperAlignmentHeadsPreset.LargeV2,
        "large-v3" => WhisperAlignmentHeadsPreset.LargeV3,
        "large-v3-turbo" => WhisperAlignmentHeadsPreset.LargeV3Turbo,
        _ => null,
    };

    /// <summary>The size half of a model name: <c>openai/whisper-base</c> is <c>base</c>.</summary>
    private static string Size(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var name = model.Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        const string Repository = "whisper-";
        if (name.StartsWith(Repository, StringComparison.OrdinalIgnoreCase))
        {
            name = name[Repository.Length..];
        }

        return name.ToLowerInvariant();
    }
}
