namespace FoulFilterNet.Domain;

/// <summary>
/// How far a cut window is widened either side of the matched speech.
/// </summary>
/// <remarks>
/// Asymmetric on purpose: speech onsets are detected late, so the tail needs
/// more room than the head. These constants are load-bearing - the filtergraph
/// strings the tests assert on are built from the windows they produce.
/// </remarks>
public sealed record HitPadding(double Pre, double Post)
{
    /// <summary>0.15 s before, 0.25 s after, as in <c>Legacy/src/pipeline.py</c>.</summary>
    public static HitPadding Default { get; } = new(0.15, 0.25);
}

/// <summary>
/// Turns raw Hits into the final cut windows: pad, drop inversions, sort, and
/// merge anything that overlaps. Ported from <c>pipeline.merge_hits</c>.
/// </summary>
public sealed class HitMerger
{
    private readonly HitPadding _padding;

    /// <summary>Merge with <paramref name="padding"/>, or <see cref="HitPadding.Default"/>.</summary>
    public HitMerger(HitPadding? padding = null) => _padding = padding ?? HitPadding.Default;

    /// <summary>The padding this merger applies.</summary>
    public HitPadding Padding => _padding;

    /// <summary>
    /// Pad every hit, discard the ones that do not describe a forward window,
    /// then merge overlaps into single windows whose phrases are joined with
    /// <c>+</c>. A merged window keeps the word index of the hit it started from.
    /// </summary>
    public IReadOnlyList<Hit> Merge(IReadOnlyList<Hit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var padded = new List<Hit>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.End <= hit.Start)
            {
                continue;
            }

            var start = Math.Max(0.0, hit.Start - _padding.Pre);
            var end = hit.End + _padding.Post;
            if (end <= start)
            {
                continue;
            }

            padded.Add(hit with { Start = Times.Round(start), End = Times.Round(end) });
        }

        // OrderBy is a stable sort, matching Python's list.sort, so hits padded
        // to the same start stay in the order they were found.
        var ordered = padded.OrderBy(h => h.Start).ToList();

        var merged = new List<Hit>(ordered.Count);
        foreach (var hit in ordered)
        {
            if (merged.Count == 0 || hit.Start > merged[^1].End)
            {
                merged.Add(hit);
                continue;
            }

            var previous = merged[^1];
            merged[^1] = previous with
            {
                End = Math.Max(previous.End, hit.End),
                Phrase = $"{previous.Phrase}+{hit.Phrase}",
            };
        }

        return merged;
    }
}
