namespace FoulFilterNet.Transcription;

/// <summary>
/// Whisper model naming. The configuration surface accepts a bare size
/// (<c>base</c>, <c>large-v3-turbo</c>) for brevity; engines want a fully
/// qualified repository id. A name that already names a repository - anything
/// containing a slash, including a third-party one - passes through untouched.
/// </summary>
/// <remarks>
/// Ported from <c>normalize_model_name</c> in <c>Legacy/src/transcriber.py</c>.
/// The names are kept in Hugging Face form even though the engine is now
/// whisper.cpp, so existing configuration keeps working; mapping a repository
/// id onto a GGML weights file is the concrete engine's job (T13).
/// </remarks>
public static class ModelNames
{
    /// <summary>The size used when nothing is configured.</summary>
    public const string DefaultSize = "base";

    /// <summary>Repository prefix applied to a bare size.</summary>
    public const string Prefix = "openai/whisper-";

    /// <summary>The fully qualified default model id.</summary>
    public const string Default = Prefix + DefaultSize;

    /// <summary>
    /// Expand a bare model size to a full model id. Null, empty or whitespace
    /// falls back to <see cref="DefaultSize"/>; a slash-qualified name is
    /// returned trimmed but otherwise unchanged.
    /// </summary>
    public static string Normalize(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Default;
        }

        return trimmed.Contains('/', StringComparison.Ordinal) ? trimmed : Prefix + trimmed;
    }
}
