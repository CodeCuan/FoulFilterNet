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
        _options.ResolvedTranscriptDirectory.ShouldBe(Path.Combine(_options.DataDirectory, "transcripts"));
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
    public void UsesIt() => _options.ResolvedTranscriptDirectory.ShouldBe(Path.Combine("y", "cache"));

    [Fact]
    public void LeavesTheJobDirectoriesUnderTheDataRoot() =>
        _options.ScratchDirectory.ShouldBe(Path.Combine("x", "data", "scratch"));
}
