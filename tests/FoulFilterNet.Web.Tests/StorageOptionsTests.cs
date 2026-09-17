namespace FoulFilterNet.Web.Tests;

public class WhenStorageIsLeftAtItsDefaults
{
    private readonly StorageOptions _options = new();

    public WhenStorageIsLeftAtItsDefaults()
    {
        _options.DataDirectory.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void KeepsThePythonsUploadCap() => _options.MaxUploadMegabytes.ShouldBe(4096);

    [Fact]
    public void ExpressesThatCapInBytesForTheWriter() =>
        _options.MaxUploadBytes.ShouldBe(4096L * 1024 * 1024);

    [Fact]
    public void PutsTranscriptsUnderTheDataDirectory() =>
        _options.ResolvedTranscriptDirectory.ShouldBe(
            Path.Combine(_options.DataDirectory, "transcripts")
        );

    /// <summary>
    /// Not the Python's <c>/data</c>, which on native Windows is the root of the
    /// current drive.
    /// </summary>
    [Fact]
    public void KeepsDataInThePerUserDefault() =>
        _options.DataDirectory.ShouldBe(Pipeline.DataLocations.DefaultDataDirectory);

    [Fact]
    public void ReadsTheBadWordsListFromTheDataDirectory() =>
        _options.BadWordsPath.ShouldBe(Path.Combine(_options.DataDirectory, "bad_words.txt"));
}

/// <summary>
/// <c>appsettings.json</c> writes the storage keys out blank so they are
/// discoverable; blank has to mean "the default", not "the current directory".
/// </summary>
public class WhenStorageIsConfiguredBlank
{
    private readonly StorageOptions _options;

    public WhenStorageIsConfiguredBlank()
    {
        _options = new StorageOptions { DataDirectory = "", BadWordsPath = " " };

        _options.ShouldNotBeNull();
    }

    [Fact]
    public void UsesTheDefaultDataDirectory() =>
        _options.DataDirectory.ShouldBe(Pipeline.DataLocations.DefaultDataDirectory);

    [Fact]
    public void UsesTheDefaultBadWordsList() =>
        _options.BadWordsPath.ShouldBe(
            Path.Combine(Pipeline.DataLocations.DefaultDataDirectory, "bad_words.txt")
        );
}

/// <summary>
/// Moving the data directory moves the list with it, which the Python's fixed
/// <c>/data/bad_words.txt</c> did not.
/// </summary>
public class WhenOnlyTheDataDirectoryIsMoved
{
    private readonly StorageOptions _options;

    public WhenOnlyTheDataDirectoryIsMoved()
    {
        _options = new StorageOptions { DataDirectory = Path.Combine("x", "data") };

        _options.ShouldNotBeNull();
    }

    [Fact]
    public void MovesTheBadWordsListToo() =>
        _options.BadWordsPath.ShouldBe(Path.Combine("x", "data", "bad_words.txt"));
}

public class WhenTranscriptsAreGivenTheirOwnLocation
{
    private readonly StorageOptions _options;

    public WhenTranscriptsAreGivenTheirOwnLocation()
    {
        _options = new StorageOptions
        {
            DataDirectory = Path.Combine("x", "data"),
            TranscriptDirectory = Path.Combine("y", "cache"),
        };
    }

    [Fact]
    public void UsesIt() =>
        _options.ResolvedTranscriptDirectory.ShouldBe(Path.Combine("y", "cache"));

    [Fact]
    public void LeavesTheJobDirectoriesUnderTheDataRoot() =>
        _options.ScratchDirectory.ShouldBe(Path.Combine("x", "data", "scratch"));
}
