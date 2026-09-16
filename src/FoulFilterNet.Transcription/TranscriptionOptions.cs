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
        init => _modelDirectory = string.IsNullOrWhiteSpace(value)
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
}
