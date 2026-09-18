namespace FoulFilterNet.Sources.Tests;

public class WhenParsingASuccessfulRun
{
    private const string AudioPath = @"C:\Temp\ffn\audio.webm";

    private readonly YtDlpOutput _output;

    public WhenParsingASuccessfulRun()
    {
        _output = YtDlpOutput.Parse(
            YtDlpSamples.WithAudioPath(YtDlpSamples.SuccessOutput, AudioPath)
        );

        _output.Info.ShouldNotBeNull();
        _output.File.ShouldNotBeNull();
    }

    [Fact]
    public void ReadsTheId() => _output.Info!.Id.ShouldBe("jNQXAC9IVRw");

    [Fact]
    public void ReadsTheTitle() => _output.Info!.Title.ShouldBe("Me at the zoo");

    [Fact]
    public void ReadsTheDuration() => _output.Info!.DurationSeconds.ShouldBe(19);

    [Fact]
    public void ReadsTheLiveStatus() => _output.Info!.LiveStatus.ShouldBe("not_live");

    [Fact]
    public void IsNotLive() => _output.Info!.IsLive.ShouldBeFalse();

    [Fact]
    public void ReadsTheFilePathWithItsBackslashesUnescaped() =>
        _output.File!.FilePath.ShouldBe(AudioPath);

    [Fact]
    public void ReadsTheExtension() => _output.File!.Extension.ShouldBe("webm");

    [Fact]
    public void ReadsTheFormat() => _output.File!.FormatId.ShouldBe("251");

    [Fact]
    public void ReadsTheCodec() => _output.File!.Codec.ShouldBe("opus");
}

public class WhenParsingAUnicodeTitle
{
    private readonly YtDlpOutput _output;

    public WhenParsingAUnicodeTitle()
    {
        _output = YtDlpOutput.Parse(
            YtDlpSamples.WithAudioPath(YtDlpSamples.UnicodeTitleOutput, "a.webm")
        );

        _output.Info.ShouldNotBeNull();
    }

    [Fact]
    public void DecodesTheEscapedHangul() =>
        _output.Info!.Title.ShouldBe(YtDlpSamples.UnicodeTitle);

    [Fact]
    public void ReadsTheDuration() => _output.Info!.DurationSeconds.ShouldBe(252);
}

public class WhenParsingALivestreamTheFilterSkipped
{
    private readonly YtDlpOutput _output;

    public WhenParsingALivestreamTheFilterSkipped()
    {
        _output = YtDlpOutput.Parse(YtDlpSamples.LiveSkippedOutput);

        _output.Info.ShouldNotBeNull();
    }

    [Fact]
    public void HasNoFile() => _output.File.ShouldBeNull();

    [Fact]
    public void IsLive() => _output.Info!.IsLive.ShouldBeTrue();

    [Fact]
    public void HasNoDurationWhenTheKeyIsMissing() => _output.Info!.DurationSeconds.ShouldBeNull();

    [Fact]
    public void DecodesTheSurrogatePairIntoOneEmoji() =>
        _output.Info!.Title.ShouldBe(YtDlpSamples.LiveTitle);
}

public class WhenReadingTheLiveStatus
{
    [Theory]
    [InlineData("is_live", true)]
    [InlineData("is_upcoming", true)]
    [InlineData("not_live", false)]
    [InlineData("was_live", false)]
    [InlineData("post_live", false)]
    [InlineData(null, false)]
    public void TreatsOnlyLiveAndUpcomingAsLive(string? status, bool live) =>
        new YtDlpInfo("id", "title", 1, status).IsLive.ShouldBe(live);
}

public class WhenParsingOutputWithNothingOfOurs
{
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("[download] Destination: audio.webm\n")]
    [InlineData("{\"id\": \"jNQXAC9IVRw\"}\n")]
    [InlineData("FFN-INFOS {\"id\": \"x\"}\n")]
    [InlineData("ffn-info {\"id\": \"x\"}\n")]
    [InlineData(" FFN-INFO {\"id\": \"x\"}\n")]
    public void FindsNoInfo(string standardOutput) =>
        YtDlpOutput.Parse(standardOutput).Info.ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("[download] Destination: audio.webm\n")]
    [InlineData("{\"filepath\": \"a.webm\"}\n")]
    public void FindsNoFile(string standardOutput) =>
        YtDlpOutput.Parse(standardOutput).File.ShouldBeNull();

    [Fact]
    public void RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => YtDlpOutput.Parse(null!));
}

public class WhenAMarkedLineIsNotUsable
{
    [Theory]
    [InlineData("FFN-INFO not json\n")]
    [InlineData("FFN-INFO {\"id\": \"x\"\n")]
    [InlineData("FFN-INFO [1, 2]\n")]
    [InlineData("FFN-INFO \"jNQXAC9IVRw\"\n")]
    [InlineData("FFN-INFO null\n")]
    [InlineData("FFN-INFO\n")]
    [InlineData("FFN-INFO {\"title\": \"no id\"}\n")]
    [InlineData("FFN-INFO {\"id\": 42}\n")]
    public void IgnoresABrokenInfoLine(string standardOutput) =>
        YtDlpOutput.Parse(standardOutput).Info.ShouldBeNull();

    [Theory]
    [InlineData("FFN-FILE not json\n")]
    [InlineData("FFN-FILE {\"ext\": \"webm\"}\n")]
    [InlineData("FFN-FILE {\"filepath\": \"\"}\n")]
    [InlineData("FFN-FILE {\"filepath\": null}\n")]
    [InlineData("FFN-FILE {\"filepath\": 7}\n")]
    public void IgnoresAFileLineWithoutAPath(string standardOutput) =>
        YtDlpOutput.Parse(standardOutput).File.ShouldBeNull();
}

public class WhenOptionalFieldsAreMissingOrOdd
{
    [Fact]
    public void GivesAnEmptyTitleWhenThereIsNone() =>
        YtDlpOutput.Parse("FFN-INFO {\"id\": \"x\"}\n").Info!.Title.ShouldBeEmpty();

    [Fact]
    public void GivesAnEmptyTitleWhenItIsNull() =>
        YtDlpOutput
            .Parse("FFN-INFO {\"id\": \"x\", \"title\": null}\n")
            .Info!.Title.ShouldBeEmpty();

    [Fact]
    public void GivesNoLiveStatusWhenThereIsNone() =>
        YtDlpOutput.Parse("FFN-INFO {\"id\": \"x\"}\n").Info!.LiveStatus.ShouldBeNull();

    [Fact]
    public void ReadsAFractionalDuration() =>
        YtDlpOutput
            .Parse("FFN-INFO {\"id\": \"x\", \"duration\": 12.5}\n")
            .Info!.DurationSeconds.ShouldBe(12.5);

    [Fact]
    public void KeepsAZeroDuration() =>
        YtDlpOutput
            .Parse("FFN-INFO {\"id\": \"x\", \"duration\": 0}\n")
            .Info!.DurationSeconds.ShouldBe(0);

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("\"19\"")]
    [InlineData("true")]
    public void HasNoDurationWhenItIsNotAUsableNumber(string duration) =>
        YtDlpOutput
            .Parse($"FFN-INFO {{\"id\": \"x\", \"duration\": {duration}}}\n")
            .Info!.DurationSeconds.ShouldBeNull();

    [Fact]
    public void GivesAnEmptyExtensionWhenThereIsNone() =>
        YtDlpOutput.Parse("FFN-FILE {\"filepath\": \"a\"}\n").File!.Extension.ShouldBeEmpty();

    [Fact]
    public void GivesNoFormatWhenThereIsNone() =>
        YtDlpOutput.Parse("FFN-FILE {\"filepath\": \"a\"}\n").File!.FormatId.ShouldBeNull();

    [Fact]
    public void GivesNoCodecWhenThereIsNone() =>
        YtDlpOutput.Parse("FFN-FILE {\"filepath\": \"a\"}\n").File!.Codec.ShouldBeNull();

    [Fact]
    public void IgnoresFieldsItDoesNotKnow() =>
        YtDlpOutput.Parse("FFN-INFO {\"id\": \"x\", \"view_count\": 3}\n").Info!.Id.ShouldBe("x");
}

public class WhenLinesEndInCarriageReturns
{
    private readonly YtDlpOutput _output;

    public WhenLinesEndInCarriageReturns()
    {
        _output = YtDlpOutput.Parse(
            YtDlpSamples
                .WithAudioPath(YtDlpSamples.SuccessOutput, "a.webm")
                .ReplaceLineEndings("\r\n")
        );

        _output.Info.ShouldNotBeNull();
        _output.File.ShouldNotBeNull();
    }

    [Fact]
    public void StillReadsTheInfo() => _output.Info!.Title.ShouldBe("Me at the zoo");

    [Fact]
    public void KeepsTheCarriageReturnOutOfTheCodec() => _output.File!.Codec.ShouldBe("opus");
}

public class WhenOurLinesAreMixedWithOtherOutput
{
    private readonly YtDlpOutput _output;

    public WhenOurLinesAreMixedWithOtherOutput()
    {
        _output = YtDlpOutput.Parse(
            "[youtube] Extracting URL\n"
                + "FFN-INFO {\"id\": \"first\", \"title\": \"A\"}\n"
                + "[download] 100%\n"
                + "FFN-FILE {\"filepath\": \"one.webm\"}\n"
                + "FFN-INFO {\"id\": \"second\", \"title\": \"B\"}\n"
                + "FFN-FILE {\"filepath\": \"two.webm\"}\n"
        );

        _output.Info.ShouldNotBeNull();
        _output.File.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsTheLastInfo() => _output.Info!.Id.ShouldBe("second");

    [Fact]
    public void KeepsTheLastFile() => _output.File!.FilePath.ShouldBe("two.webm");
}
