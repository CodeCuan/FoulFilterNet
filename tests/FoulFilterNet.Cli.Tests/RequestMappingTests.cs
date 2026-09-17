using FoulFilterNet.Cli;
using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using Microsoft.Extensions.Configuration;

namespace FoulFilterNet.Cli.Tests;

/// <summary>
/// Everything the Python derived between <c>parse_args</c> and <c>run_job</c>:
/// the output path, the scratch directory beside it, and the transcript cache.
/// </summary>
public sealed class WhenMappingAMinimalCommandLineToAJobRequest
{
    private readonly JobRequest _request;

    public WhenMappingAMinimalCommandLineToAJobRequest()
    {
        var commandLine = new FoulFilterCommandLine();
        var parsed = commandLine.Parse(Path.Combine("media", "clip.mp3"), "words.txt");

        parsed.Errors.ShouldBeEmpty();

        _request = commandLine.ToRequest(parsed, _ => null);
    }

    [Fact]
    public void KeepsTheInputPath() =>
        _request.InputPath.ShouldBe(Path.Combine("media", "clip.mp3"));

    [Fact]
    public void KeepsTheBadWordsPath() => _request.BadWordsPath.ShouldBe("words.txt");

    [Fact]
    public void NamesTheOutputAfterTheInput() =>
        _request.OutputPath.ShouldBe(Path.Combine("media", "censored_clip.mp3"));

    [Fact]
    public void PutsScratchBesideTheOutput() =>
        _request.ScratchDirectory.ShouldBe(Path.Combine("media", ".clip_scratch"));

    /// <summary>
    /// The same cache Web uses, rather than the Python's <c>/data/transcripts</c>,
    /// which on native Windows was the root of whichever drive the terminal was on.
    /// </summary>
    [Fact]
    public void SharesWebsDefaultTranscriptDirectory() =>
        _request.TranscriptDirectory.ShouldBe(
            Path.Combine(DataLocations.DefaultDataDirectory, "transcripts")
        );

    [Fact]
    public void SilencesByDefault() => _request.CensorMethod.ShouldBe(CensorMethod.Silence);

    [Fact]
    public void DoesNotExportTheTranscript() => _request.Debug.ShouldBeFalse();

    [Fact]
    public void DoesNotRescan() => _request.Rescan.ShouldBeFalse();

    [Fact]
    public void Renders() => _request.Render.ShouldBeTrue();
}

/// <summary>A file in the working directory has no directory part to join onto.</summary>
public sealed class WhenTheInputHasNoDirectory
{
    private readonly JobRequest _request;

    public WhenTheInputHasNoDirectory()
    {
        var commandLine = new FoulFilterCommandLine();
        _request = commandLine.ToRequest(commandLine.Parse("clip.wav", "words.txt"), _ => null);
    }

    [Fact]
    public void WritesTheOutputIntoTheCurrentDirectory() =>
        _request.OutputPath.ShouldBe(Path.Combine(".", "censored_clip.wav"));

    [Fact]
    public void ScratchesInTheCurrentDirectory() =>
        _request.ScratchDirectory.ShouldBe(Path.Combine(".", ".clip_scratch"));
}

/// <summary>
/// An explicit <c>--output</c> moves the scratch directory with it, but the
/// scratch name still comes from the input's base name - exactly as the Python
/// composed it.
/// </summary>
public sealed class WhenAnExplicitOutputIsGiven
{
    private readonly JobRequest _request;

    public WhenAnExplicitOutputIsGiven()
    {
        var commandLine = new FoulFilterCommandLine();
        _request = commandLine.ToRequest(
            commandLine.Parse(
                Path.Combine("media", "clip.mp3"),
                "words.txt",
                "--output",
                Path.Combine("elsewhere", "clean.mp3")
            ),
            _ => null
        );
    }

    [Fact]
    public void UsesIt() => _request.OutputPath.ShouldBe(Path.Combine("elsewhere", "clean.mp3"));

    [Fact]
    public void MovesScratchBesideIt() =>
        _request.ScratchDirectory.ShouldBe(Path.Combine("elsewhere", ".clip_scratch"));
}

/// <summary>The configured transcript cache wins over the data directory's.</summary>
public sealed class WhenTheTranscriptDirectoryIsConfigured
{
    private readonly JobRequest _request;

    public WhenTheTranscriptDirectoryIsConfigured()
    {
        var commandLine = new FoulFilterCommandLine();
        _request = commandLine.ToRequest(
            commandLine.Parse("clip.mp3", "words.txt"),
            key =>
                key switch
                {
                    ConfigurationKeys.TranscriptDirectory => "cache",
                    ConfigurationKeys.DataDirectory => "data",
                    _ => null,
                }
        );
    }

    [Fact]
    public void UsesIt() => _request.TranscriptDirectory.ShouldBe("cache");
}

/// <summary>Moving the data directory moves the CLI's cache, exactly as it moves Web's.</summary>
public sealed class WhenOnlyTheDataDirectoryIsConfigured
{
    private readonly JobRequest _request;

    public WhenOnlyTheDataDirectoryIsConfigured()
    {
        var commandLine = new FoulFilterCommandLine();
        _request = commandLine.ToRequest(
            commandLine.Parse("clip.mp3", "words.txt"),
            key => key == ConfigurationKeys.DataDirectory ? "data" : null
        );
    }

    [Fact]
    public void CachesTranscriptsUnderIt() =>
        _request.TranscriptDirectory.ShouldBe(Path.Combine("data", "transcripts"));
}

/// <summary>
/// The whole route a legacy deployment takes: the Python's variable names,
/// through the shared translator, into the lookup the host hands the mapper.
/// </summary>
public sealed class WhenALegacyEnvironmentDrivesTheCommandLine
{
    private readonly JobRequest _request;

    public WhenALegacyEnvironmentDrivesTheCommandLine()
    {
        var configuration = new ConfigurationBuilder()
            .AddLegacyEnvironmentVariables(
                new Dictionary<string, string>
                {
                    ["TRANSCRIPT_DIR"] = "cache",
                    ["CENSOR_METHOD"] = "delete",
                }
            )
            .Build();

        var commandLine = new FoulFilterCommandLine();
        _request = commandLine.ToRequest(
            commandLine.Parse("clip.mp3", "words.txt"),
            key => configuration[key]
        );
    }

    [Fact]
    public void TakesTheTranscriptDirectory() => _request.TranscriptDirectory.ShouldBe("cache");

    [Fact]
    public void TakesTheCensorMethod() => _request.CensorMethod.ShouldBe(CensorMethod.Remove);
}

/// <summary>The three analysis flags, and the one that suppresses the edit.</summary>
public sealed class WhenEveryAnalysisFlagIsGiven
{
    private readonly JobRequest _request;

    public WhenEveryAnalysisFlagIsGiven()
    {
        var commandLine = new FoulFilterCommandLine();
        var parsed = commandLine.Parse(
            "clip.mp3",
            "words.txt",
            "--debug",
            "--rescan",
            "--no_edit",
            "--bleep"
        );

        parsed.Errors.ShouldBeEmpty();

        _request = commandLine.ToRequest(parsed, _ => null);
    }

    [Fact]
    public void ExportsTheTranscript() => _request.Debug.ShouldBeTrue();

    [Fact]
    public void RescansWithShiftedBoundaries() => _request.Rescan.ShouldBeTrue();

    [Fact]
    public void SuppressesTheRenderRatherThanAddingAParallelFlag() =>
        _request.Render.ShouldBeFalse();

    [Fact]
    public void StillResolvesTheCensorMethod() =>
        _request.CensorMethod.ShouldBe(CensorMethod.Bleep);
}

/// <summary>The parser rejects what the Python's <c>choices</c> rejected.</summary>
public sealed class WhenCensorMethodIsNotAMethod
{
    private readonly IReadOnlyList<string> _errors;

    public WhenCensorMethodIsNotAMethod()
    {
        var commandLine = new FoulFilterCommandLine();
        _errors =
        [
            .. commandLine
                .Parse("clip.mp3", "words.txt", "--censor_method", "obliterate")
                .Errors.Select(error => error.Message),
        ];
    }

    [Fact]
    public void FailsToParse() => _errors.ShouldNotBeEmpty();

    [Fact]
    public void SaysWhichValueWasWrong() =>
        _errors.ShouldContain(message => message.Contains("obliterate", StringComparison.Ordinal));
}

/// <summary>Both positional arguments are required, as argparse made them.</summary>
public sealed class WhenTheBadWordsListIsMissing
{
    private readonly IReadOnlyList<string> _errors;

    public WhenTheBadWordsListIsMissing()
    {
        var commandLine = new FoulFilterCommandLine();
        _errors = [.. commandLine.Parse("clip.mp3").Errors.Select(error => error.Message)];
    }

    [Fact]
    public void FailsToParse() => _errors.ShouldNotBeEmpty();
}
