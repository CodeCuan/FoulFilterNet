using FoulFilterNet.Evaluation;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>The manifest shape the fixture generator writes, trimmed to two fixtures.</summary>
public sealed class ParsingAManifest
{
    private const string Json = """
        {
            "generatedBy": "tests/fixtures/generate-fixtures.ps1",
            "fixtures": [
                { "file": "single_hit.mp3", "kind": "audio", "duration": 8.388,
                  "hits": [ { "phrase": "damn", "start": 3.41, "end": 3.897 } ] },
                { "file": "false_positive.mp3", "kind": "audio", "duration": 4.608,
                  "hits": [ { "phrase": "hoe", "start": 2.634, "end": 3.082 } ] }
            ]
        }
        """;

    private readonly IReadOnlyList<FixtureTruth> _fixtures;

    public ParsingAManifest() =>
        _fixtures = GroundTruthManifest.Parse(Json, innocentFixtures: ["false_positive.mp3"]);

    [Fact]
    public void ReadsEveryFixture() => _fixtures.Select(f => f.File).ShouldBe(["single_hit.mp3", "false_positive.mp3"]);

    [Fact]
    public void ReadsTheKind() => _fixtures[0].Kind.ShouldBe("audio");

    [Fact]
    public void ReadsThePlantedSpan() => _fixtures[0].Spans.Single().ShouldBe(new PlantedSpan("damn", 3.41, 3.897));

    [Fact]
    public void MarksAnInnocentFixturesSpansAsNotProfanity() =>
        _fixtures[1].Spans.Single().IsProfanity.ShouldBeFalse();
}

/// <summary>
/// A fixture the generator wrote with no hits serialises its list as empty, and
/// a phrase is compared in the matcher's spelling however it was written.
/// </summary>
public sealed class ParsingAManifestWithAnEmptyFixtureAndAnUnnormalisedPhrase
{
    private const string Json = """
        { "fixtures": [
            { "file": "clean_speech.mp3", "kind": "audio", "duration": 6.732, "hits": [ ] },
            { "file": "phrase_hit.mp3", "kind": "audio", "duration": 7.8,
              "hits": [ { "phrase": "Go To Hell", "start": 3.505, "end": 4.227 } ] }
        ] }
        """;

    private readonly IReadOnlyList<FixtureTruth> _fixtures;

    public ParsingAManifestWithAnEmptyFixtureAndAnUnnormalisedPhrase() =>
        _fixtures = GroundTruthManifest.Parse(Json, innocentFixtures: []);

    [Fact]
    public void KeepsTheEmptyFixture() => _fixtures[0].Spans.ShouldBeEmpty();

    [Fact]
    public void NormalisesThePhrase() => _fixtures[1].Spans.Single().Phrase.ShouldBe("go to hell");
}

/// <summary>
/// The repository's own manifest: ten profanities and one garden tool - the
/// eleven spans ADR-0006 measured.
/// </summary>
public sealed class TheRepositorysManifest
{
    private readonly IReadOnlyList<FixtureTruth> _fixtures;

    public TheRepositorysManifest() =>
        _fixtures = GroundTruthManifest.Load(
            GroundTruthManifest.Locate(AppContext.BaseDirectory), GroundTruthManifest.DefaultInnocentFixtures);

    [Fact]
    public void HasSevenFixtures() => _fixtures.Count.ShouldBe(7);

    [Fact]
    public void PlantsTenProfanities() => _fixtures.SelectMany(f => f.Spans).Count(s => s.IsProfanity).ShouldBe(10);

    [Fact]
    public void PlantsOneInnocentWord() => _fixtures.SelectMany(f => f.Spans).Count(s => !s.IsProfanity).ShouldBe(1);

    [Fact]
    public void ListsEveryPhraseForTheDefaultBadWordsList() =>
        GroundTruthManifest.Phrases(_fixtures).ShouldBe(["damn", "go to hell", "hoe"]);
}
