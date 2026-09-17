using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.Pipeline;

/// <summary>One of the Python's environment variables and the .NET key it now sets.</summary>
public sealed record LegacyVariable(string Name, string Key);

/// <summary>One of the Python's environment variables that no longer means anything, and why.</summary>
public sealed record RetiredVariable(string Name, string Reason);

/// <summary>
/// The Python's environment variable names, mapped onto .NET configuration keys
/// in the one place both hosts share.
/// </summary>
/// <remarks>
/// <para>
/// <c>appsettings.json</c> is the primary source. A legacy variable overrides it,
/// exactly as a variable overrode the Python's defaults, so an existing
/// <c>.env</c> keeps working. A standard <c>Section__Key</c> variable for the same
/// key wins over the legacy name: it is the more deliberate spelling.
/// </para>
/// <para>
/// Values are translated where the Python's reading differs from the binder's:
/// its booleans were string comparisons, and its Smart Cut mode was "local or
/// else Google". A blank variable is unset, because the legacy compose file wrote
/// <c>X=${X}</c> for every variable and the Python read almost all of them as
/// <c>os.getenv(X) or default</c> - so an old <c>.env</c> behaves as it did.
/// </para>
/// <para>
/// <c>GOOGLE_API_KEY</c> is deliberately absent: <c>AddSmartCut</c> reads it by
/// that name and nothing else, so it has no second route into configuration.
/// </para>
/// </remarks>
public static partial class LegacyEnvironmentVariables
{
    private static readonly string[] UnloadTruthy = ["1", "true", "yes"];

    /// <summary>The variables that still configure something.</summary>
    public static IReadOnlyList<LegacyVariable> Mapped { get; } =
    [
        new("DATA_DIR", ConfigurationKeys.DataDirectory),
        new("TRANSCRIPT_DIR", ConfigurationKeys.TranscriptDirectory),
        new("BAD_WORDS_PATH", ConfigurationKeys.BadWordsPath),
        new("MAX_UPLOAD_MB", ConfigurationKeys.MaxUploadMegabytes),
        new("CENSOR_METHOD", ConfigurationKeys.CensorMethod),
        new("WHISPER_MODEL", ConfigurationKeys.Model),
        new("WHISPER_LANGUAGE", ConfigurationKeys.Language),
        new("UNLOAD_MODELS_AFTER_JOB", ConfigurationKeys.UnloadAfterJob),
        new("AI_ENHANCE", ConfigurationKeys.SmartCutEnabled),
        new("AI_MODE", ConfigurationKeys.SmartCutMode),
        new("LOCAL_LLM_URL", ConfigurationKeys.LocalUrl),
        new("LOCAL_LLM_MODEL", ConfigurationKeys.LocalModel),
    ];

    /// <summary>
    /// The variables that existed to work around ROCm, PyTorch or the old
    /// two-model design (analysis §9.1). Setting one is not an error, but it is
    /// warned about, because it no longer does what its author meant.
    /// </summary>
    public static IReadOnlyList<RetiredVariable> Retired { get; } =
    [
        new(
            "ALIGN_DEVICE",
            "alignment is part of whisper.cpp transcription now; to keep the GPU free use UNLOAD_MODELS_AFTER_JOB, "
                + "or Transcription__Device=Cpu to move all of transcription off it"
        ),
        new("WHISPER_MULTI_GPU", "whisper.cpp runs on one device; there is no sharding to switch"),
        new("WHISPER_ATTN", "there is no PyTorch attention implementation to override"),
        new("ANALYSIS_CHUNK_SIZE", "nothing read it, in the Python or here"),
        new("TORCH_INDEX_URL", "there are no PyTorch wheels to choose between"),
        new("HSA_OVERRIDE_GFX_VERSION", "a ROCm workaround for RDNA2 cards; the target is CUDA"),
        new(
            "HF_HOME",
            "models are GGML files under Transcription__ModelDirectory, not a Hugging Face cache"
        ),
        new("MIOPEN_USER_DB_PATH", "a ROCm kernel cache; the target is CUDA"),
        new("TRITON_CACHE_DIR", "there is no Triton JIT"),
    ];

    /// <summary>The .NET keys the legacy variables set in <paramref name="environment"/>, with values translated.</summary>
    public static IReadOnlyDictionary<string, string?> Translate(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var variables = Read(environment);
        var translated = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in Mapped)
        {
            if (
                !variables.TryGetValue(variable.Name, out var value)
                || string.IsNullOrWhiteSpace(value)
            )
            {
                continue;
            }

            // Section__Key (or Section:Key) for the same setting is the standard
            // provider's to apply, and it wins.
            if (variables.ContainsKey(variable.Key))
            {
                continue;
            }

            translated[variable.Key] = TranslateValue(variable.Name, value.Trim());
        }

        return translated;
    }

    /// <summary>The retired variables <paramref name="environment"/> actually sets.</summary>
    public static IReadOnlyList<RetiredVariable> RetiredIn(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var variables = Read(environment);

        return
        [
            .. Retired.Where(variable =>
                variables.TryGetValue(variable.Name, out var value)
                && !string.IsNullOrWhiteSpace(value)
            ),
        ];
    }

    /// <summary>Warns once for each retired variable the process environment sets.</summary>
    public static void WarnAboutRetiredVariables(ILogger logger) =>
        WarnAboutRetiredVariables(logger, Environment.GetEnvironmentVariables());

    /// <summary>Warns once for each retired variable <paramref name="environment"/> sets.</summary>
    public static void WarnAboutRetiredVariables(ILogger logger, IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var variable in RetiredIn(environment))
        {
            LogRetired(logger, variable.Name, variable.Reason);
        }
    }

    /// <summary>
    /// Adds the process environment's legacy variables. Call it after the
    /// defaults, so it sits above <c>appsettings.json</c>.
    /// </summary>
    public static IConfigurationBuilder AddLegacyEnvironmentVariables(
        this IConfigurationBuilder builder
    ) => builder.AddLegacyEnvironmentVariables(null);

    /// <summary>Adds the legacy variables in <paramref name="environment"/>, or the process's when null.</summary>
    public static IConfigurationBuilder AddLegacyEnvironmentVariables(
        this IConfigurationBuilder builder,
        IDictionary? environment
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new LegacyEnvironmentConfigurationSource(environment));
    }

    private static string TranslateValue(string name, string value) =>
        name switch
        {
            // (os.getenv("AI_ENHANCE") or "").lower() == "true"
            "AI_ENHANCE" => value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? "true"
                : "false",

            // flag.strip().lower() in ("1", "true", "yes")
            "UNLOAD_MODELS_AFTER_JOB" => UnloadTruthy.Contains(
                value,
                StringComparer.OrdinalIgnoreCase
            )
                ? "true"
                : "false",

            // if AI_MODE == "local": ... else Gemini
            "AI_MODE" => value.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? "Local"
                : "Google",

            _ => value,
        };

    /// <summary>
    /// Environment names compared case-insensitively, as .NET's own environment
    /// provider compares them; the colon spelling is normalised too, so both
    /// <c>Storage__DataDirectory</c> and <c>Storage:DataDirectory</c> are found.
    /// </summary>
    private static Dictionary<string, string?> Read(IDictionary environment)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string name)
            {
                variables[name.Replace("__", ":", StringComparison.Ordinal)] =
                    entry.Value as string;
                variables[name] = entry.Value as string;
            }
        }

        return variables;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "{Variable} is set but no longer has any effect: {Reason}."
    )]
    private static partial void LogRetired(ILogger logger, string variable, string reason);
}

/// <summary>The configuration source for <see cref="LegacyEnvironmentVariables"/>.</summary>
public sealed class LegacyEnvironmentConfigurationSource(IDictionary? environment)
    : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new LegacyEnvironmentConfigurationProvider(environment);
}

/// <summary>
/// Reads the environment when configuration loads, so a host picks up the
/// variables it was started with rather than the ones present when it was built.
/// </summary>
public sealed class LegacyEnvironmentConfigurationProvider(IDictionary? environment)
    : ConfigurationProvider
{
    public override void Load() =>
        Data = new Dictionary<string, string?>(
            LegacyEnvironmentVariables.Translate(
                environment ?? Environment.GetEnvironmentVariables()
            ),
            StringComparer.OrdinalIgnoreCase
        );
}
