namespace FoulFilterNet.Books.Tests;

public sealed class PartialWordFileTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void AMissingFileIsNoPartialWords() =>
        PartialWordFile.Load(_path + ".missing").ShouldBeEmpty();

    [Fact]
    public void WordsAreNormalizedAndCommentsSkipped()
    {
        File.WriteAllLines(_path, ["# a comment", "", " FUCK ", "fuck"]);

        PartialWordFile.Load(_path).ShouldBe(["fuck"]);
    }

    [Fact]
    public void APhraseIsRefusedBecauseItCanNeverSitInsideAWord()
    {
        File.WriteAllLines(_path, ["two words"]);

        Should.Throw<InvalidDataException>(() => PartialWordFile.Load(_path));
    }
}
