using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>Which compute device a transcriber should try to use.</summary>
public enum TranscriptionDevice
{
    /// <summary>Use the GPU when one is usable, fall back to the CPU otherwise.</summary>
    Auto = 0,

    /// <summary>Force CUDA; a machine without it is a configuration error.</summary>
    Cuda,

    /// <summary>Force the CPU - the escape hatch for when VRAM is spoken for.</summary>
    Cpu,
}

/// <summary>
/// Everything a transcriber needs that is not the audio itself.
/// </summary>
/// <remarks>
/// <see cref="Model"/> normalizes on assignment, so configuration may carry
/// either a bare size or a full repository id and every consumer sees the same
/// spelling. See <see cref="ModelNames"/>.
/// </remarks>
public sealed record TranscriptionOptions
{
    private readonly string _model = ModelNames.Default;
    private readonly string? _language;
    private readonly string _modelDirectory = WhisperModelSource.DefaultDirectory;
    private readonly string? _priorityWordsPath;

    /// <summary>The model id, always fully qualified.</summary>
    public string Model
    {
        get => _model;
        init => _model = ModelNames.Normalize(value);
    }

    /// <summary>
    /// Language code to force, or null to let the model detect it. A blank
    /// value is treated as null, matching the Python's <c>or None</c> chain.
    /// </summary>
    public string? Language
    {
        get => _language;
        init => _language = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Device preference; the engine may still fall back to the CPU.</summary>
    public TranscriptionDevice Device { get; init; } = TranscriptionDevice.Auto;

    /// <summary>
    /// Where GGML weights are kept. A relative path is resolved against the
    /// process directory. Weights are a gigabyte and more, gitignored and never
    /// committed, so this is the one piece of configuration a fresh machine
    /// usually has to be told about; a blank value falls back to the default.
    /// </summary>
    public string ModelDirectory
    {
        get => _modelDirectory;
        init =>
            _modelDirectory = string.IsNullOrWhiteSpace(value)
                ? WhisperModelSource.DefaultDirectory
                : value.Trim();
    }

    /// <summary>
    /// Release the model after every job rather than keeping it resident
    /// (<c>UNLOAD_MODELS_AFTER_JOB</c>). Off by default: on a dedicated machine
    /// a resident model is faster, and the flag exists so a local Smart Cut LLM
    /// can share the card.
    /// </summary>
    public bool UnloadAfterJob { get; init; }

    /// <summary>
    /// Re-hear each window in short sub-windows prompted with the Priority Word
    /// List, and add the priority words found there that the primary pass missed
    /// (docs/05-crosstalk-plan.md). On by default; an empty list also turns it off.
    /// </summary>
    public bool PriorityPass { get; init; } = true;

    /// <summary>
    /// Where the Priority Word List is read from, or null for
    /// <c>priority_words.txt</c> in the data directory. A blank value is null.
    /// </summary>
    public string? PriorityWordsPath
    {
        get => _priorityWordsPath;
        init => _priorityWordsPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Seconds of pre-roll on a priority word's cut window. Zero (or unset) is
    /// the measured default, 0.25 s; see <see cref="PriorityTuning"/>.
    /// </summary>
    public double PriorityPaddingPre { get; init; } = CutPadding.DefaultPriorityPadding.Pre;

    /// <summary>
    /// Seconds of post-roll on a priority word's cut window. Zero (or unset) is
    /// the measured default, 0.5 s; see <see cref="PriorityTuning"/>.
    /// </summary>
    public double PriorityPaddingPost { get; init; } = CutPadding.DefaultPriorityPadding.Post;

    /// <summary>
    /// The shortest a priority word's Hit is taken to be before it is padded: a
    /// shorter one is grown backward from its reported end to this length. Zero
    /// (or unset) is the measured default, 0.8 s; see <see cref="PriorityTuning"/>.
    /// </summary>
    public double PriorityMinimumCutSeconds { get; init; } =
        CutPadding.DefaultPriorityMinimumSeconds;

    /// <summary>
    /// Seconds of audio in each sub-window the Priority Word Pass re-hears a
    /// window in. Zero (or unset) is the measured default, 5 s. Trades speed
    /// against detection; see <see cref="PrioritySubWindows"/>.
    /// </summary>
    public double PrioritySubWindowSeconds { get; init; } = PrioritySubWindows.DefaultLengthSeconds;

    /// <summary>
    /// Seconds between the starts of those sub-windows; at most one sub-window,
    /// or the audio between them is never heard by the pass. Zero (or unset) is
    /// the measured default, 2.5 s. Trades speed against detection.
    /// </summary>
    public double PrioritySubWindowStepSeconds { get; init; } =
        PrioritySubWindows.DefaultStepSeconds;
}
