using System.Text.Json;

namespace FoulFilterNet.Sources;

/// <summary>The video's metadata, from the <see cref="YtDlpArguments.InfoMarker"/> line.</summary>
/// <param name="Id">YouTube's ID for what was extracted.</param>
/// <param name="Title">The title, decoded; empty when yt-dlp gave none.</param>
/// <param name="DurationSeconds">The duration, or null when absent or not a usable number
/// (a livestream has no <c>duration</c> key at all).</param>
/// <param name="LiveStatus">yt-dlp's <c>live_status</c>: <c>not_live</c>,
/// <c>is_live</c>, <c>is_upcoming</c>, <c>was_live</c> or <c>post_live</c>.</param>
public sealed record YtDlpInfo(string Id, string Title, double? DurationSeconds, string? LiveStatus)
{
    /// <summary>
    /// A livestream or a stream that has not started: nothing a Watch Session
    /// can fetch whole. A finished stream (<c>was_live</c>) is an ordinary video.
    /// </summary>
    public bool IsLive => LiveStatus is "is_live" or "is_upcoming";
}

/// <summary>The downloaded file, from the <see cref="YtDlpArguments.FileMarker"/> line.</summary>
/// <param name="FilePath">Where yt-dlp says it put the file.</param>
/// <param name="Extension">The container extension; empty when not reported.</param>
/// <param name="FormatId">YouTube's format id, if reported.</param>
/// <param name="Codec">The audio codec, if reported.</param>
public sealed record YtDlpFile(string FilePath, string Extension, string? FormatId, string? Codec);

/// <summary>
/// What a fetch printed on standard output: our two marked JSON lines, found
/// among anything else. A line that is marked but unusable is ignored rather
/// than thrown on, so it reads as "not reported" and the caller's checks (no
/// file line means no audio) decide.
/// </summary>
/// <param name="Info">The last usable info line, or null.</param>
/// <param name="File">The last usable file line, or null.</param>
public sealed record YtDlpOutput(YtDlpInfo? Info, YtDlpFile? File)
{
    /// <summary>Read the marked lines from yt-dlp's standard output.</summary>
    public static YtDlpOutput Parse(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);

        YtDlpInfo? info = null;
        YtDlpFile? file = null;

        foreach (var rawLine in standardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (Payload(line, YtDlpArguments.InfoMarker) is { } infoJson)
            {
                info = ReadInfo(infoJson) ?? info;
            }
            else if (Payload(line, YtDlpArguments.FileMarker) is { } fileJson)
            {
                file = ReadFile(fileJson) ?? file;
            }
        }

        return new YtDlpOutput(info, file);
    }

    /// <summary>The JSON after <c>&lt;marker&gt; </c> at the very start of the line.</summary>
    private static string? Payload(string line, string marker) =>
        line.StartsWith(marker + " ", StringComparison.Ordinal)
            ? line[(marker.Length + 1)..]
            : null;

    private static YtDlpInfo? ReadInfo(string json) =>
        ReadObject(
            json,
            root =>
                String(root, "id") is { Length: > 0 } id
                    ? new YtDlpInfo(
                        id,
                        String(root, "title") ?? string.Empty,
                        Duration(root),
                        String(root, "live_status")
                    )
                    : null
        );

    private static YtDlpFile? ReadFile(string json) =>
        ReadObject(
            json,
            root =>
                String(root, "filepath") is { Length: > 0 } path
                    ? new YtDlpFile(
                        path,
                        String(root, "ext") ?? string.Empty,
                        String(root, "format_id"),
                        String(root, "acodec")
                    )
                    : null
        );

    private static T? ReadObject<T>(string json, Func<JsonElement, T?> read)
        where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? read(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Duration(JsonElement root) =>
        root.TryGetProperty("duration", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var seconds)
        && seconds >= 0
        && double.IsFinite(seconds)
            ? seconds
            : null;
}
