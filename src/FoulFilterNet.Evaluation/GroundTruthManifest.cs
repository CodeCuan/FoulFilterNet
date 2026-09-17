using System.Text.Json;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Evaluation;

/// <summary>
/// Reads <c>tests/fixtures/media/manifest.json</c>, whose spans are exact by
/// construction (<c>tests/fixtures/generate-fixtures.ps1</c>).
/// </summary>
/// <remarks>
/// The manifest records every span the generator planted, the garden hoe of
/// <c>false_positive.mp3</c> included - it says <em>where</em> a word is, not
/// whether it should be censored. Which fixtures hold innocent words is
/// therefore the evaluation's knowledge, passed in, rather than the manifest's.
/// </remarks>
public static class GroundTruthManifest
{
    /// <summary>Where the manifest sits relative to the repository root.</summary>
    public static readonly string RelativePath = Path.Combine(
        "tests",
        "fixtures",
        "media",
        "manifest.json"
    );

    /// <summary>The fixture planted to be flagged wrongly: "hoe", a garden tool.</summary>
    public static IReadOnlyList<string> DefaultInnocentFixtures { get; } = ["false_positive.mp3"];

    public static IReadOnlyList<FixtureTruth> Parse(
        string json,
        IEnumerable<string> innocentFixtures
    )
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(innocentFixtures);

        var innocent = innocentFixtures.ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var fixtures = new List<FixtureTruth>();

        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var file =
                fixture.GetProperty("file").GetString()
                ?? throw new InvalidDataException("A fixture in the manifest has no file name.");
            var profane = !innocent.Contains(file);

            var spans = new List<PlantedSpan>();
            if (
                fixture.TryGetProperty("hits", out var hits)
                && hits.ValueKind is JsonValueKind.Array
            )
            {
                foreach (var hit in hits.EnumerateArray())
                {
                    spans.Add(
                        new PlantedSpan(
                            Tokenizer.Normalize(
                                hit.GetProperty("phrase").GetString() ?? string.Empty
                            ),
                            hit.GetProperty("start").GetDouble(),
                            hit.GetProperty("end").GetDouble(),
                            profane
                        )
                    );
                }
            }

            fixtures.Add(
                new FixtureTruth(
                    file,
                    fixture.TryGetProperty("kind", out var kind)
                        ? kind.GetString() ?? string.Empty
                        : string.Empty,
                    fixture.TryGetProperty("duration", out var duration)
                        ? duration.GetDouble()
                        : 0.0,
                    spans
                )
            );
        }

        return fixtures;
    }

    public static IReadOnlyList<FixtureTruth> Load(
        string path,
        IEnumerable<string> innocentFixtures
    ) => Parse(File.ReadAllText(path), innocentFixtures);

    /// <summary>
    /// The repository's manifest, found by walking up from
    /// <paramref name="startDirectory"/> - which is how both the test assembly
    /// and a <c>dotnet run</c> from anywhere in the checkout reach it.
    /// </summary>
    /// <exception cref="FileNotFoundException">No ancestor holds the manifest.</exception>
    public static string Locate(string startDirectory)
    {
        for (
            var directory = new DirectoryInfo(startDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            var candidate = Path.Combine(directory.FullName, RelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {RelativePath} above {startDirectory}.");
    }

    /// <summary>
    /// Every distinct phrase planted, in order: the Bad Words List an evaluation
    /// uses unless it is given another, so that nothing outside the ground truth
    /// can be reported.
    /// </summary>
    public static IReadOnlyList<string> Phrases(IReadOnlyList<FixtureTruth> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        return fixtures
            .SelectMany(f => f.Spans)
            .Select(s => s.Phrase)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
