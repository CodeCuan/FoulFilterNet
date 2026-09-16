namespace FoulFilterNet.Web.Tests;

/// <summary>
/// ADR-0002: a job is ephemeral, so the half-finished uploads and scratch files
/// a previous process left behind are stale the moment it died. Transcripts are
/// an expensive cache and outputs are what the operator came back for, so both
/// survive the restart that wipes the other two.
/// </summary>
public class WhenStartingOverAPreviousRunsFiles : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly StorageOptions _options;

    public WhenStartingOverAPreviousRunsFiles()
    {
        _options = new StorageOptions { DataDirectory = _data.Path };

        Write(_options.UploadDirectory, "abc123__book.mp3");
        Write(Path.Combine(_options.UploadDirectory, "partial"), "chunk.tmp");
        Write(Path.Combine(_options.ScratchDirectory, "abc123"), "extracted_audio.m4a");
        Write(_options.OutputDirectory, "censored_book.mp3");
        Write(_options.ResolvedTranscriptDirectory, "d41d8__book.mp3.json");

        StorageHousekeeping.Prepare(_options);
    }

    [Fact]
    public void DropsUploadsThatBelongedToJobsThatNoLongerExist() =>
        File.Exists(Path.Combine(_options.UploadDirectory, "abc123__book.mp3")).ShouldBeFalse();

    /// <summary>
    /// Scratch is one directory per job, not a flat pile of files, so a wipe that
    /// only removed files would leave every job's working directory behind.
    /// </summary>
    [Fact]
    public void DropsWholeSubdirectoriesToo() =>
        Directory.Exists(Path.Combine(_options.ScratchDirectory, "abc123")).ShouldBeFalse();

    [Fact]
    public void DropsSubdirectoriesOfUploadsAsWell() =>
        Directory.Exists(Path.Combine(_options.UploadDirectory, "partial")).ShouldBeFalse();

    [Fact]
    public void KeepsTheTranscriptCacheThatCostAGpuToProduce() =>
        File.Exists(Path.Combine(_options.ResolvedTranscriptDirectory, "d41d8__book.mp3.json")).ShouldBeTrue();

    [Fact]
    public void KeepsTheOutputsSomeoneCameBackFor() =>
        File.Exists(Path.Combine(_options.OutputDirectory, "censored_book.mp3")).ShouldBeTrue();

    [Fact]
    public void LeavesTheWipedDirectoriesInPlaceToBeWrittenTo() =>
        Directory.Exists(_options.UploadDirectory).ShouldBeTrue();

    private static void Write(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), "stale");
    }

    public void Dispose()
    {
        _data.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// First boot against an empty volume. Nothing downstream creates these - an
/// upload opens a file straight into <c>uploads/</c> - so the service has to.
/// </summary>
public class WhenTheDataDirectoryHasNeverBeenUsed : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly StorageOptions _options;

    public WhenTheDataDirectoryHasNeverBeenUsed()
    {
        _options = new StorageOptions { DataDirectory = Path.Combine(_root.Path, "data") };

        Directory.Exists(_options.DataDirectory).ShouldBeFalse();

        StorageHousekeeping.Prepare(_options);
    }

    [Fact]
    public void CreatesSomewhereToPutUploads() =>
        Directory.Exists(_options.UploadDirectory).ShouldBeTrue();

    [Fact]
    public void CreatesSomewhereToPutOutputs() =>
        Directory.Exists(_options.OutputDirectory).ShouldBeTrue();

    [Fact]
    public void CreatesSomewhereToWorkInPassing() =>
        Directory.Exists(_options.ScratchDirectory).ShouldBeTrue();

    [Fact]
    public void CreatesTheTranscriptCache() =>
        Directory.Exists(_options.ResolvedTranscriptDirectory).ShouldBeTrue();

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Something still holding a scratch file open - a virus scanner, an editor, a
/// previous process that has not finished dying - must not stop the service
/// booting. The Python swallowed exactly this and started anyway.
/// </summary>
public class WhenAScratchFileCannotBeDeleted : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly StorageOptions _options;
    private readonly FileStream _held;

    public WhenAScratchFileCannotBeDeleted()
    {
        _options = new StorageOptions { DataDirectory = _data.Path };
        Directory.CreateDirectory(_options.ScratchDirectory);

        File.WriteAllText(Path.Combine(_options.ScratchDirectory, "free.wav"), "stale");
        _held = new FileStream(
            Path.Combine(_options.ScratchDirectory, "held.wav"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        // The act is the assertion: anything thrown here fails every fact below.
        StorageHousekeeping.Prepare(_options);
    }

    [Fact]
    public void StillRemovesEverythingItCan() =>
        File.Exists(Path.Combine(_options.ScratchDirectory, "free.wav")).ShouldBeFalse();

    [Fact]
    public void StillLeavesAScratchDirectoryBehind() =>
        Directory.Exists(_options.ScratchDirectory).ShouldBeTrue();

    public void Dispose()
    {
        _held.Dispose();
        _data.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The same thing again through the host, which is where it actually has to
/// happen: the test fixture no longer prepares anything, so if the service did
/// not do this on startup an upload would have nowhere to land.
/// </summary>
public class WhenTheHostStartsWithFilesFromALastRun : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly string _transcripts;

    public WhenTheHostStartsWithFilesFromALastRun()
    {
        _transcripts = Path.Combine(_app.DataDirectory, "transcripts");

        Directory.CreateDirectory(Path.Combine(_app.DataDirectory, "uploads"));
        Directory.CreateDirectory(_transcripts);
        File.WriteAllText(Path.Combine(_app.DataDirectory, "uploads", "old__book.mp3"), "stale");
        File.WriteAllText(Path.Combine(_transcripts, "d41d8__book.mp3.json"), "{}");

        // Nothing has started yet; creating the client is what boots the host.
        _client = _app.CreateClient();
    }

    [Fact]
    public void TheHostWipesTheStaleUploadItself() =>
        File.Exists(Path.Combine(_app.UploadDirectory, "old__book.mp3")).ShouldBeFalse();

    [Fact]
    public void TheHostKeepsTheTranscriptCacheItself() =>
        File.Exists(Path.Combine(_transcripts, "d41d8__book.mp3.json")).ShouldBeTrue();

    [Fact]
    public void TheHostCreatesTheUploadDirectoryItself() =>
        Directory.Exists(_app.UploadDirectory).ShouldBeTrue();

    [Fact]
    public void TheHostCreatesTheOutputDirectoryItself() =>
        Directory.Exists(_app.OutputDirectory).ShouldBeTrue();

    /// <summary>
    /// The point of the whole exercise: an upload arriving at a freshly booted
    /// service has somewhere to be written without anyone having prepared it.
    /// </summary>
    [Fact]
    public async Task AnUploadStillLands()
    {
        var jobId = await Api.UploadOneAsync(_client);

        jobId.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}
