using FoulFilterNet.Domain;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>A Bad Words List file in its own temporary directory.</summary>
internal sealed class BadWordsFile : IDisposable
{
    private static readonly DateTime Written = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private int _edits;

    public BadWordsFile(params string[] lines)
    {
        Directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ffn-badwords-tests",
            Guid.NewGuid().ToString("N")
        );
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "bad_words.txt");
        if (lines.Length > 0)
        {
            Write(lines);
        }
    }

    public string Directory { get; }

    public string Path { get; }

    /// <summary>
    /// Rewrite the file and move its last-write time on by a minute, so the
    /// change is seen however coarse the file system's timestamps are.
    /// </summary>
    public void Write(params string[] lines)
    {
        File.WriteAllLines(Path, lines);
        File.SetLastWriteTimeUtc(Path, Written.AddMinutes(++_edits));
    }

    /// <summary>Rewrite the file but leave its last-write time and length as they were.</summary>
    public void WriteUnnoticed(params string[] lines)
    {
        var stamp = File.GetLastWriteTimeUtc(Path);
        File.WriteAllLines(Path, lines);
        File.SetLastWriteTimeUtc(Path, stamp);
    }

    public void Delete() => File.Delete(Path);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException) { }
    }
}

public sealed class WhenTheBadWordsFileIsReadForTheFirstTime : IDisposable
{
    private readonly BadWordsFile _file = new("# comment", "Damn", "go to hell", "");
    private readonly FileBadWordsSource _source;
    private readonly BadWordsList _list;

    public WhenTheBadWordsFileIsReadForTheFirstTime()
    {
        _source = new FileBadWordsSource(_file.Path);
        _list = _source.GetCurrent();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void NormalizesThePhrases() =>
        _list.Phrases.Order(StringComparer.Ordinal).ShouldBe(["damn", "go to hell"]);

    [Fact]
    public void KnowsItsPath() => _source.Path.ShouldBe(_file.Path);

    [Fact]
    public void ServesTheSameListWhileTheFileIsUnchanged() =>
        _source.GetCurrent().ShouldBeSameAs(_list);
}

public sealed class WhenTheBadWordsFileIsEdited : IDisposable
{
    private readonly BadWordsFile _file = new("damn");
    private readonly BadWordsList _before;
    private readonly BadWordsList _after;

    public WhenTheBadWordsFileIsEdited()
    {
        var source = new FileBadWordsSource(_file.Path);
        _before = source.GetCurrent();

        _file.Write("crap", "shit");
        _after = source.GetCurrent();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void ReadsTheNewPhrases() =>
        _after.Phrases.Order(StringComparer.Ordinal).ShouldBe(["crap", "shit"]);

    [Fact]
    public void LeavesTheOldListAsItWas() => _before.Phrases.ShouldBe(["damn"]);
}

public sealed class WhenTheBadWordsFileChangesOnlyInLength : IDisposable
{
    private readonly BadWordsFile _file = new("damn");
    private readonly BadWordsList _after;

    public WhenTheBadWordsFileChangesOnlyInLength()
    {
        var source = new FileBadWordsSource(_file.Path);
        source.GetCurrent();

        // Saved twice within the timestamp's resolution: same time, new length.
        _file.WriteUnnoticed("damn", "crap");
        _after = source.GetCurrent();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void StillNoticesTheChange() => _after.Contains("crap").ShouldBeTrue();
}

public sealed class WhenTheBadWordsFileChangesWithNothingToNotice : IDisposable
{
    private readonly BadWordsFile _file = new("damn");
    private readonly BadWordsList _before;
    private readonly BadWordsList _after;

    public WhenTheBadWordsFileChangesWithNothingToNotice()
    {
        var source = new FileBadWordsSource(_file.Path);
        _before = source.GetCurrent();

        // Same length, same time: a stat cannot tell, so no read is made.
        _file.WriteUnnoticed("crap");
        _after = source.GetCurrent();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void DoesNotReadItAgain() => _after.ShouldBeSameAs(_before);
}

public sealed class WhenTheBadWordsFileIsEmptiedByAnEdit : IDisposable
{
    private readonly BadWordsFile _file = new("damn");
    private readonly FileBadWordsSource _source;
    private readonly BadWordsList _before;
    private readonly BadWordsList _during;

    public WhenTheBadWordsFileIsEmptiedByAnEdit()
    {
        _source = new FileBadWordsSource(_file.Path);
        _before = _source.GetCurrent();

        _file.Write("# nothing but a comment");
        _during = _source.GetCurrent();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void KeepsTheLastGoodList() => _during.ShouldBeSameAs(_before);

    [Fact]
    public void PicksUpTheNextGoodEdit()
    {
        _file.Write("shit");

        _source.GetCurrent().Phrases.ShouldBe(["shit"]);
    }
}

public sealed class WhenTheBadWordsFileIsDeletedAfterBeingRead : IDisposable
{
    private readonly BadWordsFile _file = new("damn");
    private readonly FileBadWordsSource _source;
    private readonly BadWordsList _before;

    public WhenTheBadWordsFileIsDeletedAfterBeingRead()
    {
        _source = new FileBadWordsSource(_file.Path);
        _before = _source.GetCurrent();
        _file.Delete();
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void KeepsTheLastGoodList() => _source.GetCurrent().ShouldBeSameAs(_before);

    [Fact]
    public void ReadsItOnceItIsBack()
    {
        _file.Write("crap");

        _source.GetCurrent().Phrases.ShouldBe(["crap"]);
    }
}

public sealed class WhenThereIsNoBadWordsFile : IDisposable
{
    private readonly BadWordsFile _file = new();
    private readonly FileBadWordsSource _source;

    public WhenThereIsNoBadWordsFile()
    {
        _source = new FileBadWordsSource(_file.Path);
    }

    public void Dispose() => _file.Dispose();

    [Fact]
    public void Throws() => Should.Throw<InvalidOperationException>(_source.GetCurrent);

    [Fact]
    public void SaysWhichFile() =>
        Should
            .Throw<InvalidOperationException>(_source.GetCurrent)
            .Message.ShouldContain(_file.Path);

    [Fact]
    public void ReadsItOnceItAppears()
    {
        Should.Throw<InvalidOperationException>(_source.GetCurrent);
        _file.Write("damn");

        _source.GetCurrent().Phrases.ShouldBe(["damn"]);
    }
}

public sealed class WhenTheBadWordsFileHasNoUsableLineFromTheStart : IDisposable
{
    private readonly BadWordsFile _file = new("", "# just comments");

    public void Dispose() => _file.Dispose();

    [Fact]
    public void Throws() =>
        Should.Throw<InvalidOperationException>(new FileBadWordsSource(_file.Path).GetCurrent);
}

public sealed class WhenABadWordsSourceIsGivenNoPath
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesABlankPath(string path) =>
        Should.Throw<ArgumentException>(() => new FileBadWordsSource(path));

    [Fact]
    public void RefusesNull() =>
        Should.Throw<ArgumentNullException>(() => new FileBadWordsSource(null!));
}
