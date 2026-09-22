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
/// merge anything that overlaps. Ported from <c>pipeline.merge_hits</c>, with
/// wider windows for priority words (<see cref="Domain.CutPadding"/>).
/// </summary>
public sealed class HitMerger
{
    private readonly CutPadding _padding;

    /// <summary>Merge with <paramref name="padding"/> for every Hit, or <see cref="HitPadding.Default"/>.</summary>
    public HitMerger(HitPadding? padding = null)
        : this(padding is null ? Domain.CutPadding.Default : new CutPadding(padding)) { }

    /// <summary>Merge padding each Hit as <paramref name="padding"/> says for its phrase.</summary>
    public HitMerger(CutPadding padding)
    {
        ArgumentNullException.ThrowIfNull(padding);
        _padding = padding;
    }

    /// <summary>The padding this merger applies to ordinary (non-priority) Hits.</summary>
    public HitPadding Padding => _padding.Ordinary;

    /// <summary>How this merger widens each Hit.</summary>
    public CutPadding CutPadding => _padding;

    /// <summary>
    /// Pad every hit (priority words wider, see <see cref="Domain.CutPadding"/>), discard the ones that do not describe a forward window,
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

            var (widenedStart, end) = _padding.Widen(hit.Phrase, hit.Start, hit.End);
            var start = Math.Max(0.0, widenedStart);
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
