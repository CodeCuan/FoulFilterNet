using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// The Python's <c>/data</c> is the root of the current drive on native Windows.
/// The default is a per-user directory instead.
/// </summary>
public sealed class WhenNothingConfiguresTheDataDirectory
{
    private readonly string _default = DataLocations.DefaultDataDirectory;

    public WhenNothingConfiguresTheDataDirectory()
    {
        _default.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LivesInThePerUserApplicationDataFolder() =>
        _default.ShouldBe(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "FoulFilterNet"));

    [Fact]
    public void IsNotTheContainerPath() => _default.ShouldNotBe("/data");
}

public sealed class WhenResolvingTheTranscriptDirectory
{
    private readonly string _data = Path.Combine("x", "data");

    public WhenResolvingTheTranscriptDirectory()
    {
        _data.ShouldNotBeEmpty();
    }

    [Fact]
    public void DefaultsUnderTheDataDirectory() =>
        DataLocations.TranscriptDirectory(_data, null).ShouldBe(Path.Combine(_data, "transcripts"));

    [Fact]
    public void TreatsABlankOverrideAsUnset() =>
        DataLocations.TranscriptDirectory(_data, "  ").ShouldBe(Path.Combine(_data, "transcripts"));

    [Fact]
    public void HonoursAnOverride() =>
        DataLocations.TranscriptDirectory(_data, "cache").ShouldBe("cache");

    [Fact]
    public void FallsBackToTheDefaultDataDirectoryWhenThatIsBlank() =>
        DataLocations.TranscriptDirectory("", null)
            .ShouldBe(Path.Combine(DataLocations.DefaultDataDirectory, "transcripts"));
}

public sealed class WhenResolvingTheBadWordsList
{
    private readonly string _data = Path.Combine("x", "data");

    public WhenResolvingTheBadWordsList()
    {
        _data.ShouldNotBeEmpty();
    }

    [Fact]
    public void DefaultsIntoTheDataDirectory() =>
        DataLocations.BadWordsPath(_data, null).ShouldBe(Path.Combine(_data, "bad_words.txt"));

    [Fact]
    public void HonoursAnOverride() => DataLocations.BadWordsPath(_data, "words.txt").ShouldBe("words.txt");
}
