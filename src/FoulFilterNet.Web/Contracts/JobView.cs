using FoulFilterNet.Domain;
using FoulFilterNet.Jobs;

namespace FoulFilterNet.Web.Contracts;

/// <summary>
/// What a client is allowed to know about a job.
/// <para>
/// The Python filtered its record dictionaries by stripping any key ending in
/// <c>_path</c> on the way out, which is one forgotten suffix away from serving
/// the server's filesystem layout. Here the paths live on
/// <see cref="JobRecord.Request"/> and there is simply no member on this type
/// that could carry one.
/// </para>
/// </summary>
public sealed record JobView
{
    public required string Id { get; init; }

    /// <summary>Spelled without an underscore because the front end reads <c>job.filename</c>.</summary>
    public required string Filename { get; init; }

    public required string Status { get; init; }

    public required string Stage { get; init; }

    public int Progress { get; init; }

    public required string Detail { get; init; }

    public required string CensorMethod { get; init; }

    public bool Debug { get; init; }

    public bool Rescan { get; init; }

    public IReadOnlyList<HitView> Hits { get; init; } = [];

    public string? DownloadUrl { get; init; }

    public static JobView From(JobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new JobView
        {
            Id = record.Id,
            Filename = record.FileName,
            Status = WireNames.Of(record.Status),
            Stage = record.Stage,
            Progress = record.Progress,
            Detail = record.Detail,
            CensorMethod = WireNames.Of(record.Request.CensorMethod),
            Debug = record.Request.Debug,
            Rescan = record.Request.Rescan,
            Hits = [.. record.Hits.Select(hit => new HitView(hit.Phrase, hit.Start, hit.End))],
            DownloadUrl = record.DownloadUrl,
        };
    }
}

/// <summary>One censored window, as the UI lists it.</summary>
public sealed record HitView(string Phrase, double Start, double End);

/// <summary>The shape the front end reads a failure out of (<c>data.detail</c>).</summary>
public sealed record ErrorResponse(string Detail);

/// <summary>What <c>POST /upload</c> answers with.</summary>
public sealed record UploadResponse(IReadOnlyList<string> JobIds, string Message);

/// <summary>What <c>DELETE /jobs/{id}</c> answers with.</summary>
public sealed record CancelResponse(bool Cancelled);

/// <summary>The feature flags the UI reads once, at load.</summary>
/// <param name="WebVideo">
/// W10, appended after the four fields the batch UI reads so they keep their
/// names and order: whether the tools web video needs are installed.
/// </param>
public sealed record ConfigView(
    IReadOnlyList<string> CensorMethods,
    bool AiEnhance,
    string WhisperModel,
    int MaxUploadMb,
    WebVideoView WebVideo
);

/// <summary>
/// The lower-case spellings the HTTP surface and the front end share. Kept as
/// an explicit mapping rather than <c>ToString().ToLower()</c> so renaming an
/// enum member cannot silently change the wire format.
/// </summary>
public static partial class WireNames
{
    public static string Of(JobStatus status) =>
        status switch
        {
            JobStatus.Queued => "queued",
            JobStatus.Processing => "processing",
            JobStatus.Completed => "completed",
            JobStatus.Failed => "failed",
            JobStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    public static string Of(CensorMethod method) =>
        method switch
        {
            CensorMethod.Silence => "silence",
            CensorMethod.Bleep => "bleep",
            CensorMethod.Remove => "remove",
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    /// <summary>The three the UI offers, in the order it offers them.</summary>
    public static IReadOnlyList<string> CensorMethods { get; } = ["silence", "bleep", "remove"];

    public static bool TryParseCensorMethod(string? value, out CensorMethod method)
    {
        switch (value)
        {
            case "silence":
                method = CensorMethod.Silence;
                return true;
            case "bleep":
                method = CensorMethod.Bleep;
                return true;
            case "remove":
                method = CensorMethod.Remove;
                return true;
            default:
                method = CensorMethod.Silence;
                return false;
        }
    }
}
