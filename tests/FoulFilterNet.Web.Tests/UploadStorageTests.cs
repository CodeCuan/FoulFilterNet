using FoulFilterNet.Web.Uploads;

namespace FoulFilterNet.Web.Tests;

public class WhenSavingAnUploadThatFitsTheLimit : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _destination;
    private readonly long _written;

    public WhenSavingAnUploadThatFitsTheLimit()
    {
        _destination = Path.Combine(_directory.Path, "book.mp3");
        using var source = new MemoryStream(new byte[3_000_000]);

        _written = UploadStorage.SaveAsync(source, _destination, maxBytes: 4_000_000)
            .GetAwaiter().GetResult();

        File.Exists(_destination).ShouldBeTrue();
    }

    [Fact]
    public void ReportsHowMuchItWrote() => _written.ShouldBe(3_000_000);

    [Fact]
    public void WritesEveryByte() => new FileInfo(_destination).Length.ShouldBe(3_000_000);

    [Fact]
    public async Task HandlesAnEmptyUpload()
    {
        var empty = Path.Combine(_directory.Path, "empty.mp3");
        using var source = new MemoryStream();

        var written = await UploadStorage.SaveAsync(
            source, empty, maxBytes: 10, TestContext.Current.CancellationToken);

        written.ShouldBe(0);
    }

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The cap has to be enforced while streaming rather than after: the whole
/// point is to never hold a 5 GB upload on disk, let alone in memory.
/// </summary>
public class WhenAnUploadExceedsTheLimit : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _destination;
    private readonly UploadTooLargeException _thrown;

    public WhenAnUploadExceedsTheLimit()
    {
        _destination = Path.Combine(_directory.Path, "huge.mp3");
        using var source = new MemoryStream(new byte[4_000_000]);

        _thrown = Should.Throw<UploadTooLargeException>(() =>
            UploadStorage.SaveAsync(source, _destination, maxBytes: 1_000_000).GetAwaiter().GetResult());

        _thrown.ShouldNotBeNull();
    }

    [Fact]
    public void RemovesThePartialFile() => File.Exists(_destination).ShouldBeFalse();

    [Fact]
    public void ReportsTheLimitItBrokeSoTheClientCanSayWhy() =>
        _thrown.MaxBytes.ShouldBe(1_000_000);

    [Fact]
    public void ExplainsWhatWentWrong() => _thrown.Message.ShouldContain("exceeds");

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenAnUploadFailsPartWayThrough : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _destination;

    public WhenAnUploadFailsPartWayThrough()
    {
        _destination = Path.Combine(_directory.Path, "broken.mp3");
        using var source = new FaultingStream(faultAfter: 1024);

        Should.Throw<IOException>(() =>
            UploadStorage.SaveAsync(source, _destination, maxBytes: long.MaxValue)
                .GetAwaiter().GetResult());
    }

    [Fact]
    public void LeavesNothingBehind() => File.Exists(_destination).ShouldBeFalse();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ffn-web-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A client that hangs up mid-upload.</summary>
internal sealed class FaultingStream(int faultAfter) : Stream
{
    private int _served;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_served >= faultAfter)
        {
            throw new IOException("The client hung up.");
        }

        var served = Math.Min(count, faultAfter - _served);
        _served += served;
        return served;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
