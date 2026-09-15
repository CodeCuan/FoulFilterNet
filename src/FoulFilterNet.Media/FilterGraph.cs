using System.Globalization;
using System.Text;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Media;

/// <summary>
/// Builds the three FFmpeg filtergraphs that render a <see cref="CensorMethod"/>.
/// Pure string construction: no process is started here and nothing touches the
/// filesystem, which is what lets the graphs be asserted character-for-character
/// against the Python implementation these were ported from.
/// </summary>
/// <remarks>
/// Every timestamp is rendered through <see cref="Times"/>. <see cref="Silence"/>
/// interpolates raw floats the way Python's <c>repr</c> does (<c>1.0</c>, not
/// <c>1</c>); <see cref="Bleep"/> and <see cref="Remove"/> use three fixed decimal
/// places. FFmpeg accepts either spelling, so a mistake here is invisible at
/// runtime and only a string comparison catches it. Never call
/// <see cref="double.ToString()"/> in this file.
/// </remarks>
public static class FilterGraph
{
    /// <summary>Label carrying the finished audio out of a bleep or remove graph.</summary>
    public const string OutputLabel = "[out]";

    /// <summary>Label carrying the finished audio out of a silence graph once it has been wrapped.</summary>
    public const string SilenceOutputLabel = "[aout]";

    /// <summary>Shortest tone a bleep will generate, in seconds.</summary>
    private const double MinimumBleepDuration = 0.05;

    /// <summary>Slack allowed when deciding whether a trailing span is worth keeping, in seconds.</summary>
    private const double TailEpsilon = 0.001;

    /// <summary>
    /// Zero the volume over every hit window, comma-joined. The graph is
    /// unlabelled: callers wrap it as <c>[0:a]…[aout]</c>.
    /// </summary>
    public static string Silence(IReadOnlyList<Hit> hits)
    {
        RequireHits(hits);
        return VolumeChain(hits);
    }

    /// <summary>
    /// Mute each hit window and mix a delayed 1 kHz tone over it. Everything is
    /// normalised to 44.1 kHz stereo first so <c>amix</c> sees uniform streams,
    /// and <c>normalize=0</c> keeps the untouched audio at full volume.
    /// </summary>
    public static string Bleep(IReadOnlyList<Hit> hits)
    {
        RequireHits(hits);

        var graph = new StringBuilder()
            .Append("[0:a]aformat=sample_rates=44100:channel_layouts=stereo,")
            .Append(VolumeChain(hits))
            .Append("[base]");

        var mixLabels = new StringBuilder("[base]");
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var duration = Math.Max(MinimumBleepDuration, hit.End - hit.Start);
            var delayMilliseconds = (int)(hit.Start * 1000);
            var index = i.ToString(CultureInfo.InvariantCulture);

            graph.Append(CultureInfo.InvariantCulture, $";sine=frequency=1000:duration={Times.ToFixed(duration)}[s{index}]")
                 .Append(CultureInfo.InvariantCulture, $";[s{index}]adelay={delayMilliseconds.ToString(CultureInfo.InvariantCulture)}|{delayMilliseconds.ToString(CultureInfo.InvariantCulture)}[b{index}]");
            mixLabels.Append(CultureInfo.InvariantCulture, $"[b{index}]");
        }

        var inputs = (hits.Count + 1).ToString(CultureInfo.InvariantCulture);
        graph.Append(CultureInfo.InvariantCulture, $";{mixLabels}amix=inputs={inputs}:duration=first:normalize=0{OutputLabel}");
        return graph.ToString();
    }

    /// <summary>
    /// Trim out every hit by concatenating the spans between them. Audio only —
    /// cutting a video's audio would desynchronise the picture.
    /// </summary>
    /// <param name="hits">Windows to excise. Need not be sorted.</param>
    /// <param name="totalDurationSeconds">
    /// The file's length, when known. Supplying it lets the builder drop a
    /// trailing span that would be empty, and is the only way it can tell that
    /// the hits consume the whole file.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The hit list is empty, or nothing would survive the cut.
    /// </exception>
    public static string Remove(IReadOnlyList<Hit> hits, double? totalDurationSeconds = null)
    {
        RequireHits(hits);

        var ordered = hits.OrderBy(hit => hit.Start).ToList();
        var parts = new List<string>();
        var concatRefs = new StringBuilder();
        var cursor = 0.0;
        var kept = 0;

        foreach (var hit in ordered)
        {
            if (hit.Start > cursor)
            {
                parts.Add($"[0:a]atrim=start={Times.ToFixed(cursor)}:end={Times.ToFixed(hit.Start)},asetpts=PTS-STARTPTS[clip{kept.ToString(CultureInfo.InvariantCulture)}]");
                concatRefs.Append(CultureInfo.InvariantCulture, $"[clip{kept.ToString(CultureInfo.InvariantCulture)}]");
                kept++;
            }

            cursor = Math.Max(cursor, hit.End);
        }

        var hasTail = totalDurationSeconds is null || cursor < totalDurationSeconds.Value - TailEpsilon;
        if (hasTail)
        {
            parts.Add($"[0:a]atrim=start={Times.ToFixed(cursor)},asetpts=PTS-STARTPTS[clip{kept.ToString(CultureInfo.InvariantCulture)}]");
            concatRefs.Append(CultureInfo.InvariantCulture, $"[clip{kept.ToString(CultureInfo.InvariantCulture)}]");
            kept++;
        }

        if (kept == 0)
        {
            throw new ArgumentException(
                "Hits cover the entire file; nothing would remain.", nameof(hits));
        }

        return string.Join(';', parts)
            + $";{concatRefs}concat=n={kept.ToString(CultureInfo.InvariantCulture)}:v=0:a=1{OutputLabel}";
    }

    /// <summary>
    /// Wrap an unlabelled <see cref="Silence"/> chain so it reads a file's audio
    /// stream and publishes <see cref="SilenceOutputLabel"/>.
    /// </summary>
    public static string LabelSilence(string silenceChain) => $"[0:a]{silenceChain}{SilenceOutputLabel}";

    private static string VolumeChain(IReadOnlyList<Hit> hits) =>
        string.Join(',', hits.Select(hit =>
            $"volume=enable='between(t,{Times.ToRepr(hit.Start)},{Times.ToRepr(hit.End)})':volume=0"));

    private static void RequireHits(IReadOnlyList<Hit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            throw new ArgumentException("No hits provided.", nameof(hits));
        }
    }
}
