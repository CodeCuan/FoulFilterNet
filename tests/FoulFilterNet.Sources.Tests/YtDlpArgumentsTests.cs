namespace FoulFilterNet.Sources.Tests;

public class WhenBuildingTheFetchArguments
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "ffn-watch");

    private readonly VideoRef _video = VideoRef.Create("youtube", "jNQXAC9IVRw");
    private readonly IReadOnlyList<string> _arguments;

    public WhenBuildingTheFetchArguments()
    {
        _arguments = YtDlpArguments.ForFetch(_video, Directory);

        _arguments.ShouldNotBeEmpty();
        _arguments.ShouldAllBe(a => a != null);
    }

    [Fact]
    public void IsExactlyTheOneCallThatReportsAndDownloads() =>
        _arguments.ShouldBe([
            "--ignore-config",
            "--no-playlist",
            "--format",
            "bestaudio",
            "--match-filters",
            "!is_live",
            "--no-progress",
            "--no-simulate",
            "--color",
            "never",
            "--paths",
            Directory,
            "--output",
            "audio.%(ext)s",
            "--print",
            "pre_process:FFN-INFO %(.{id,title,duration,live_status})j",
            "--print",
            "after_move:FFN-FILE %(.{filepath,ext,format_id,acodec})j",
            "--",
            "https://www.youtube.com/watch?v=jNQXAC9IVRw",
        ]);

    [Fact]
    public void EndsWithTheCanonicalWatchUrl() => _arguments[^1].ShouldBe(_video.WatchUrl);

    [Fact]
    public void EndsTheOptionsImmediatelyBeforeTheUrl() => _arguments[^2].ShouldBe("--");

    [Fact]
    public void EndsTheOptionsExactlyOnce() => _arguments.Count(a => a == "--").ShouldBe(1);

    [Fact]
    public void NeverPassesTheBareId() => _arguments.ShouldNotContain(_video.Id);

    [Fact]
    public void IgnoresEveryConfigurationFileSoNothingCanAddOptions() =>
        _arguments[0].ShouldBe("--ignore-config");

    [Fact]
    public void NeverFollowsAPlaylist() => _arguments.ShouldContain("--no-playlist");

    [Fact]
    public void DownloadsOnlyTheAudio() => ValueAfter("--format").ShouldBe("bestaudio");

    [Fact]
    public void RefusesLivestreamsInTheSameCall() =>
        ValueAfter("--match-filters").ShouldBe("!is_live");

    [Fact]
    public void DownloadsEvenThoughItPrints() => _arguments.ShouldContain("--no-simulate");

    [Fact]
    public void KeepsColourCodesOutOfTheDiagnostics() => ValueAfter("--color").ShouldBe("never");

    [Fact]
    public void WritesIntoTheGivenDirectory() => ValueAfter("--paths").ShouldBe(Directory);

    [Fact]
    public void NamesTheFileItselfRatherThanAfterTheTitle() =>
        ValueAfter("--output").ShouldBe("audio.%(ext)s");

    [Fact]
    public void ReportsTheMetadataBeforeTheMatchFilterRuns() =>
        _arguments.ShouldContain(a =>
            a.StartsWith("pre_process:FFN-INFO ", StringComparison.Ordinal)
        );

    [Fact]
    public void ReportsTheFileOnlyOnceItIsInPlace() =>
        _arguments.ShouldContain(a =>
            a.StartsWith("after_move:FFN-FILE ", StringComparison.Ordinal)
        );

    [Fact]
    public void PrintsJsonSoTitlesSurviveTheConsoleCodePage() =>
        _arguments
            .Where(a => a.Contains("FFN-", StringComparison.Ordinal))
            .ShouldAllBe(a => a.EndsWith("})j", StringComparison.Ordinal));

    [Fact]
    public void DoesNotEnableAJavaScriptRuntimeUnlessOneIsConfigured() =>
        _arguments.ShouldNotContain("--js-runtimes");

    [Fact]
    public void KeepsWarningsSoAMissingRuntimeCanBeSeen() =>
        _arguments.ShouldNotContain("--no-warnings");

    [Fact]
    public void HandsBackAFreshListEachTime() =>
        YtDlpArguments.ForFetch(_video, Directory).ShouldNotBeSameAs(_arguments);

    private string ValueAfter(string option) => _arguments[_arguments.ToList().IndexOf(option) + 1];
}

/// <summary>
/// VideoRef accepts IDs that start with a dash (W03). Handed over bare, one of
/// these would be read as an option; <c>--exec_calc</c> even reads like one.
/// The ID only ever travels inside the URL, after <c>--</c>. Checked against
/// the real binary: <c>yt-dlp -- --version</c> answers "'--version' is not a
/// valid URL" rather than printing a version.
/// </summary>
public class WhenTheIdOnTheCommandLineLooksLikeAnOption
{
    private readonly IReadOnlyList<string> _arguments;

    public WhenTheIdOnTheCommandLineLooksLikeAnOption()
    {
        _arguments = YtDlpArguments.ForFetch(
            VideoRef.Create("youtube", "--exec_calc"),
            Path.Combine(Path.GetTempPath(), "ffn-watch")
        );

        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void StillEndsWithTheWatchUrl() =>
        _arguments[^1].ShouldBe("https://www.youtube.com/watch?v=--exec_calc");

    [Fact]
    public void StillEndsTheOptionsBeforeIt() => _arguments[^2].ShouldBe("--");

    [Fact]
    public void NeverPassesTheIdAsAnArgumentOfItsOwn() =>
        _arguments.ShouldNotContain("--exec_calc");

    [Fact]
    public void AddsNoArgumentsOfItsOwn() =>
        _arguments.Count.ShouldBe(
            YtDlpArguments.ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), "x").Count
        );

    [Theory]
    [InlineData("-abcdefghij")]
    [InlineData("--exec_calc")]
    [InlineData("-----------")]
    [InlineData("-o_________")]
    public void OnlyTheUrlComesAfterTheEndOfOptions(string id)
    {
        var arguments = YtDlpArguments.ForFetch(VideoRef.Create("youtube", id), "x");

        arguments
            .SkipWhile(a => a != "--")
            .Skip(1)
            .ShouldBe([$"https://www.youtube.com/watch?v={id}"]);
    }

    [Theory]
    [InlineData("-abcdefghij")]
    [InlineData("--exec_calc")]
    [InlineData("-----------")]
    [InlineData("-o_________")]
    public void NoArgumentButTheUrlCarriesTheId(string id) =>
        YtDlpArguments
            .ForFetch(VideoRef.Create("youtube", id), "x")
            .ShouldNotContain(a =>
                a.Contains(id, StringComparison.Ordinal)
                && !a.StartsWith("https://", StringComparison.Ordinal)
            );
}

public class WhenADenoPathIsConfigured
{
    private const string Deno = @"C:\Tools\deno\deno.exe";

    private readonly IReadOnlyList<string> _arguments;

    public WhenADenoPathIsConfigured()
    {
        _arguments = YtDlpArguments.ForFetch(
            VideoRef.Create("youtube", "jNQXAC9IVRw"),
            "scratch",
            Deno
        );

        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void EnablesDenoAtThatPath() =>
        _arguments[_arguments.ToList().IndexOf("--js-runtimes") + 1].ShouldBe($"deno:{Deno}");

    [Fact]
    public void PassesItAsASeparateArgumentSoSpacesNeedNoQuoting() =>
        _arguments.ShouldContain($"deno:{Deno}");

    [Fact]
    public void PutsItAmongTheOptionsNotAfterTheEnd() =>
        _arguments
            .ToList()
            .IndexOf("--js-runtimes")
            .ShouldBeLessThan(_arguments.ToList().IndexOf("--"));

    [Fact]
    public void StillEndsWithTheUrl() =>
        _arguments[^1].ShouldBe("https://www.youtube.com/watch?v=jNQXAC9IVRw");

    [Fact]
    public void AddsExactlyTwoArguments() =>
        _arguments.Count.ShouldBe(
            YtDlpArguments.ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), "scratch").Count + 2
        );
}

public class WhenTheDenoPathIsBlank
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LeavesTheRuntimeToYtDlpsOwnSearch(string? deno) =>
        YtDlpArguments
            .ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), "scratch", deno)
            .ShouldNotContain("--js-runtimes");
}

public class WhenBuildingTheFetchArgumentsFromBadInput
{
    [Fact]
    public void RejectsANullVideo() =>
        Should.Throw<ArgumentNullException>(() => YtDlpArguments.ForFetch(null!, "scratch"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsABlankDirectory(string directory) =>
        Should.Throw<ArgumentException>(() =>
            YtDlpArguments.ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), directory)
        );

    [Fact]
    public void RejectsANullDirectory() =>
        Should.Throw<ArgumentNullException>(() =>
            YtDlpArguments.ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), null!)
        );
}

/// <summary>
/// The directory goes in through <c>--paths</c>, not into the <c>--output</c>
/// template, because the template is expanded: a <c>%</c> in a user's temp
/// path would be read as a field.
/// </summary>
public class WhenTheDirectoryContainsTemplateCharacters
{
    private const string Directory = @"C:\Users\100%(id)s\scratch";

    private readonly IReadOnlyList<string> _arguments;

    public WhenTheDirectoryContainsTemplateCharacters()
    {
        _arguments = YtDlpArguments.ForFetch(VideoRef.Create("youtube", "jNQXAC9IVRw"), Directory);

        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void PassesItVerbatimAsThePath() => _arguments.ShouldContain(Directory);

    [Fact]
    public void KeepsItOutOfTheOutputTemplate() => _arguments.ShouldContain("audio.%(ext)s");
}

public class WhenBuildingTheProbeArguments
{
    [Fact]
    public void AsksYtDlpOnlyForItsVersion() =>
        YtDlpArguments.ForVersion().ShouldBe(["--ignore-config", "--version"]);

    [Fact]
    public void AsksDenoOnlyForItsVersion() =>
        YtDlpArguments.ForDenoVersion().ShouldBe(["--version"]);
}

public class WhenNamingTheDownloadedFile
{
    [Fact]
    public void UsesAFixedStem() => YtDlpArguments.AudioFileStem.ShouldBe("audio");

    [Fact]
    public void MarksTheInfoLine() => YtDlpArguments.InfoMarker.ShouldBe("FFN-INFO");

    [Fact]
    public void MarksTheFileLine() => YtDlpArguments.FileMarker.ShouldBe("FFN-FILE");
}
