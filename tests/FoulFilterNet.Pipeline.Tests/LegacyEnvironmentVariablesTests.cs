using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// A legacy deployment's <c>.env</c>, whole, arriving under the Python's names.
/// Every variable that still means something lands on the .NET key that means it.
/// </summary>
public sealed class WhenALegacyDeploymentsEnvironmentIsTranslated
{
    private readonly IConfiguration _configuration;

    public WhenALegacyDeploymentsEnvironmentIsTranslated()
    {
        var environment = new Dictionary<string, string>
        {
            ["DATA_DIR"] = "/srv/data",
            ["TRANSCRIPT_DIR"] = "/srv/cache",
            ["BAD_WORDS_PATH"] = "/srv/words.txt",
            ["MAX_UPLOAD_MB"] = "512",
            ["CENSOR_METHOD"] = "bleep",
            ["WHISPER_MODEL"] = "large-v3-turbo",
            ["WHISPER_LANGUAGE"] = "en",
            ["UNLOAD_MODELS_AFTER_JOB"] = "True",
            ["AI_ENHANCE"] = "True",
            ["AI_MODE"] = "local",
            ["LOCAL_LLM_URL"] = "http://llm:8080/v1/chat/completions",
            ["LOCAL_LLM_MODEL"] = "gpt-oss",
        };

        _configuration = new ConfigurationBuilder()
            .AddLegacyEnvironmentVariables(environment)
            .Build();

        _configuration.AsEnumerable().ShouldNotBeEmpty();
    }

    [Fact]
    public void MapsTheDataDirectory() =>
        _configuration[ConfigurationKeys.DataDirectory].ShouldBe("/srv/data");

    [Fact]
    public void MapsTheTranscriptDirectory() =>
        _configuration[ConfigurationKeys.TranscriptDirectory].ShouldBe("/srv/cache");

    [Fact]
    public void MapsTheBadWordsList() =>
        _configuration[ConfigurationKeys.BadWordsPath].ShouldBe("/srv/words.txt");

    [Fact]
    public void MapsTheUploadCap() =>
        _configuration[ConfigurationKeys.MaxUploadMegabytes].ShouldBe("512");

    [Fact]
    public void MapsTheDefaultCensorMethod() =>
        _configuration[ConfigurationKeys.CensorMethod].ShouldBe("bleep");

    [Fact]
    public void MapsTheModel() =>
        _configuration[ConfigurationKeys.Model].ShouldBe("large-v3-turbo");

    [Fact]
    public void MapsTheLanguage() => _configuration[ConfigurationKeys.Language].ShouldBe("en");

    [Fact]
    public void MapsTheModelReleaseFlag() =>
        _configuration[ConfigurationKeys.UnloadAfterJob].ShouldBe("true");

    [Fact]
    public void MapsTheSmartCutSwitch() =>
        _configuration[ConfigurationKeys.SmartCutEnabled].ShouldBe("true");

    [Fact]
    public void MapsTheSmartCutMode() =>
        _configuration[ConfigurationKeys.SmartCutMode].ShouldBe("Local");

    [Fact]
    public void MapsTheLocalServer() =>
        _configuration[ConfigurationKeys.LocalUrl].ShouldBe("http://llm:8080/v1/chat/completions");

    [Fact]
    public void MapsTheLocalModel() =>
        _configuration[ConfigurationKeys.LocalModel].ShouldBe("gpt-oss");
}

/// <summary>
/// The point of keeping the names: an environment variable overrides the
/// committed <c>appsettings.json</c>, as it did in the Python.
/// </summary>
public sealed class WhenAppsettingsAndALegacyVariableDisagree
{
    private readonly IConfiguration _configuration;

    public WhenAppsettingsAndALegacyVariableDisagree()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [ConfigurationKeys.Model] = "base" }
            )
            .AddLegacyEnvironmentVariables(
                new Dictionary<string, string> { ["WHISPER_MODEL"] = "small" }
            )
            .Build();

        _configuration[ConfigurationKeys.Model].ShouldNotBeNull();
    }

    [Fact]
    public void TheEnvironmentWins() => _configuration[ConfigurationKeys.Model].ShouldBe("small");
}

/// <summary>
/// Both spellings at once. <c>Transcription__Model</c> is the more deliberate of
/// the two - nobody types the .NET name by accident - so the legacy name yields.
/// The stand-in for the standard environment provider sits before the legacy
/// source, which is where the hosts put it.
/// </summary>
public sealed class WhenTheDotNetSpellingIsAlsoInTheEnvironment
{
    private readonly IConfiguration _configuration;

    public WhenTheDotNetSpellingIsAlsoInTheEnvironment()
    {
        var environment = new Dictionary<string, string>
        {
            ["WHISPER_MODEL"] = "small",
            ["Transcription__Model"] = "medium",
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [ConfigurationKeys.Model] = "medium" }
            )
            .AddLegacyEnvironmentVariables(environment)
            .Build();

        _configuration[ConfigurationKeys.Model].ShouldNotBeNull();
    }

    [Fact]
    public void TheDotNetSpellingWins() =>
        _configuration[ConfigurationKeys.Model].ShouldBe("medium");
}

/// <summary>The colon spelling some shells allow counts as the .NET name too.</summary>
public sealed class WhenTheColonSpellingIsInTheEnvironment
{
    private readonly IReadOnlyDictionary<string, string?> _translated;

    public WhenTheColonSpellingIsInTheEnvironment()
    {
        _translated = LegacyEnvironmentVariables.Translate(
            new Dictionary<string, string>
            {
                ["DATA_DIR"] = "/legacy",
                ["storage:datadirectory"] = "/dotnet",
            }
        );

        _translated.ShouldNotBeNull();
    }

    [Fact]
    public void TheLegacyNameYields() =>
        _translated.ContainsKey(ConfigurationKeys.DataDirectory).ShouldBeFalse();
}

/// <summary>
/// The legacy compose file wrote <c>WHISPER_MODEL=${WHISPER_MODEL}</c>, which set every unset
/// variable to the empty string, and the Python read almost all of them with
/// <c>os.getenv(X) or default</c>. Blank therefore means "not set".
/// </summary>
public sealed class WhenLegacyVariablesAreBlank
{
    private readonly IConfiguration _configuration;

    public WhenLegacyVariablesAreBlank()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfigurationKeys.Model] = "base",
                    [ConfigurationKeys.SmartCutMode] = "Local",
                }
            )
            .AddLegacyEnvironmentVariables(
                new Dictionary<string, string> { ["WHISPER_MODEL"] = "", ["AI_MODE"] = "   " }
            )
            .Build();

        _configuration[ConfigurationKeys.Model].ShouldNotBeNull();
    }

    [Fact]
    public void LeavesTheModelFromAppsettings() =>
        _configuration[ConfigurationKeys.Model].ShouldBe("base");

    [Fact]
    public void LeavesTheModeFromAppsettings() =>
        _configuration[ConfigurationKeys.SmartCutMode].ShouldBe("Local");
}

/// <summary>
/// <c>AI_ENHANCE</c> was <c>(os.getenv("AI_ENHANCE") or "").lower() == "true"</c>:
/// only the word true turns it on.
/// </summary>
public sealed class WhenAiEnhanceIsSpelledSomeOtherWay
{
    private readonly Func<string, string?> _enabled = value =>
        LegacyEnvironmentVariables.Translate(
            new Dictionary<string, string> { ["AI_ENHANCE"] = value }
        )[ConfigurationKeys.SmartCutEnabled];

    public WhenAiEnhanceIsSpelledSomeOtherWay()
    {
        _enabled("TRUE").ShouldBe("true");
    }

    [Fact]
    public void OneIsOff() => _enabled("1").ShouldBe("false");

    [Fact]
    public void YesIsOff() => _enabled("yes").ShouldBe("false");

    [Fact]
    public void FalseIsOff() => _enabled("False").ShouldBe("false");
}

/// <summary>
/// <c>UNLOAD_MODELS_AFTER_JOB</c> accepted <c>1</c>, <c>true</c> and <c>yes</c>
/// after stripping and lowercasing - a wider set than <c>AI_ENHANCE</c>, and the
/// binder understands neither, so both are translated rather than passed on.
/// </summary>
public sealed class WhenTheModelReleaseFlagIsSpelledThePythonsWays
{
    private readonly Func<string, string?> _unload = value =>
        LegacyEnvironmentVariables.Translate(
            new Dictionary<string, string> { ["UNLOAD_MODELS_AFTER_JOB"] = value }
        )[ConfigurationKeys.UnloadAfterJob];

    public WhenTheModelReleaseFlagIsSpelledThePythonsWays()
    {
        _unload("True").ShouldBe("true");
    }

    [Fact]
    public void OneIsOn() => _unload("1").ShouldBe("true");

    [Fact]
    public void YesIsOn() => _unload(" YES ").ShouldBe("true");

    [Fact]
    public void AnythingElseIsOff() => _unload("no").ShouldBe("false");
}

/// <summary>
/// <c>ai_helper.py</c> took the local branch for <c>local</c> and Gemini for
/// everything else, so an unknown mode was Google rather than a crash.
/// </summary>
public sealed class WhenAiModeIsNotLocal
{
    private readonly Func<string, string?> _mode = value =>
        LegacyEnvironmentVariables.Translate(
            new Dictionary<string, string> { ["AI_MODE"] = value }
        )[ConfigurationKeys.SmartCutMode];

    public WhenAiModeIsNotLocal()
    {
        _mode("LOCAL").ShouldBe("Local");
    }

    [Fact]
    public void GoogleIsGoogle() => _mode("google").ShouldBe("Google");

    [Fact]
    public void AnUnknownModeIsGoogle() => _mode("openai").ShouldBe("Google");
}

/// <summary>
/// The translated values have to bind, not merely exist: an enum spelled wrong
/// or a Python boolean the binder rejects fails at the first request instead.
/// </summary>
public sealed class WhenTheTranslatedEnvironmentIsBound
{
    private readonly TranscriptionOptions _transcription;
    private readonly SmartCutOptions _smartCut;

    public WhenTheTranslatedEnvironmentIsBound()
    {
        var configuration = new ConfigurationBuilder()
            .AddLegacyEnvironmentVariables(
                new Dictionary<string, string>
                {
                    ["WHISPER_MODEL"] = "small",
                    ["UNLOAD_MODELS_AFTER_JOB"] = "yes",
                    ["AI_ENHANCE"] = "True",
                    ["AI_MODE"] = "local",
                }
            )
            .Build();

        _transcription = configuration.GetSection("Transcription").Get<TranscriptionOptions>()!;
        _smartCut = configuration.GetSection(SmartCutOptions.SectionName).Get<SmartCutOptions>()!;

        _transcription.ShouldNotBeNull();
        _smartCut.ShouldNotBeNull();
    }

    [Fact]
    public void BindsTheModel() => _transcription.Model.ShouldBe("openai/whisper-small");

    [Fact]
    public void BindsTheReleaseFlag() => _transcription.UnloadAfterJob.ShouldBeTrue();

    [Fact]
    public void BindsTheSwitch() => _smartCut.Enabled.ShouldBeTrue();

    [Fact]
    public void BindsTheMode() => _smartCut.Mode.ShouldBe(SmartCutMode.Local);
}

/// <summary>
/// The key has one deliberate source (<c>GOOGLE_API_KEY</c>, read directly by
/// <c>AddSmartCut</c>), and a configuration key is ignored on purpose. The
/// translator must not invent a second route to it.
/// </summary>
public sealed class WhenTheGoogleApiKeyIsInTheEnvironment
{
    private readonly IReadOnlyDictionary<string, string?> _translated;

    public WhenTheGoogleApiKeyIsInTheEnvironment()
    {
        _translated = LegacyEnvironmentVariables.Translate(
            new Dictionary<string, string> { ["GOOGLE_API_KEY"] = "not-a-real-key" }
        );

        _translated.ShouldNotBeNull();
    }

    [Fact]
    public void TranslatesNothing() => _translated.ShouldBeEmpty();
}

/// <summary>
/// The ROCm and PyTorch workarounds (analysis §9.1) have no meaning here. They
/// are not mapped, but they are not silently ignored either: the hosts warn.
/// </summary>
public sealed class WhenRetiredVariablesAreSet
{
    private readonly IReadOnlyList<RetiredVariable> _retired;

    public WhenRetiredVariablesAreSet()
    {
        _retired = LegacyEnvironmentVariables.RetiredIn(
            new Dictionary<string, string>
            {
                ["ALIGN_DEVICE"] = "cpu",
                ["WHISPER_MULTI_GPU"] = "False",
                ["WHISPER_ATTN"] = "",
                ["WHISPER_MODEL"] = "base",
            }
        );

        _retired.ShouldNotBeNull();
    }

    [Fact]
    public void NamesEachOneThatIsSet() =>
        _retired
            .Select(variable => variable.Name)
            .ShouldBe(["ALIGN_DEVICE", "WHISPER_MULTI_GPU"], ignoreOrder: true);

    [Fact]
    public void SaysWhatReplacesTheDeviceEscapeHatch() =>
        _retired
            .Single(variable => variable.Name == "ALIGN_DEVICE")
            .Reason.ShouldContain("Transcription__Device", Case.Sensitive);

    [Fact]
    public void TranslatesNoneOfThem() =>
        LegacyEnvironmentVariables
            .Translate(new Dictionary<string, string> { ["ALIGN_DEVICE"] = "cpu" })
            .ShouldBeEmpty();
}

/// <summary>Every retired name is documented with a reason, and none is also mapped.</summary>
public sealed class WhenTheVariableTablesAreRead
{
    private readonly IReadOnlyList<string> _mapped;
    private readonly IReadOnlyList<string> _retired;

    public WhenTheVariableTablesAreRead()
    {
        _mapped = [.. LegacyEnvironmentVariables.Mapped.Select(variable => variable.Name)];
        _retired = [.. LegacyEnvironmentVariables.Retired.Select(variable => variable.Name)];

        _mapped.ShouldNotBeEmpty();
    }

    [Fact]
    public void NoNameIsBothMappedAndRetired() => _mapped.Intersect(_retired).ShouldBeEmpty();

    [Fact]
    public void EveryRetiredNameHasAReason() =>
        LegacyEnvironmentVariables.Retired.ShouldAllBe(variable => variable.Reason.Length > 0);
}
