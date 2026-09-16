using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using FoulFilterNet.Domain;
using FoulFilterNet.Jobs;
using FoulFilterNet.Web.Contracts;
using FoulFilterNet.Web.Uploads;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Web.Endpoints;

/// <summary>The HTTP surface the batch UI drives.</summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/upload", UploadAsync);
        app.MapGet("/jobs", ListJobs);
        app.MapGet("/status/{jobId}", GetStatus);
        app.MapDelete("/jobs/{jobId}", Cancel);
        app.MapGet("/download/{jobId}", Download);
        app.MapGet("/download_zip", DownloadZip);
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        JobManager jobs,
        IOptions<StorageOptions> storage,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Expected a multipart upload.");
        }

        var options = storage.Value;
        UploadRequest upload;

        try
        {
            upload = await UploadRequestReader.ReadAsync(
                request, options.UploadDirectory, options.MaxUploadBytes, cancellationToken);
        }
        catch (UploadTooLargeException tooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, tooLarge.Message);
        }
        catch (UnsupportedUploadException unsupported)
        {
            return Problem(StatusCodes.Status400BadRequest, unsupported.Message);
        }
        catch (InvalidDataException malformed)
        {
            return Problem(StatusCodes.Status400BadRequest, malformed.Message);
        }

        if (!WireNames.TryParseCensorMethod(upload.Field("censor_method") ?? "silence", out var censorMethod))
        {
            Discard(upload);
            return Problem(StatusCodes.Status400BadRequest, "Invalid censor_method");
        }

        if (upload.Files.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "No files were uploaded.");
        }

        var jobIds = new List<string>(upload.Files.Count);
        foreach (var file in upload.Files)
        {
            jobs.Enqueue(file.JobId, file.FileName, new JobRequest
            {
                InputPath = file.InputPath,
                OutputPath = Path.Combine(options.OutputDirectory, $"censored_{file.FileName}"),
                BadWordsPath = options.BadWordsPath,
                TranscriptDirectory = options.ResolvedTranscriptDirectory,
                ScratchDirectory = Path.Combine(options.ScratchDirectory, file.JobId),
                CensorMethod = censorMethod,
                Debug = upload.Flag("debug"),
                Rescan = upload.Flag("rescan"),
            });

            jobIds.Add(file.JobId);
        }

        return Results.Ok(new UploadResponse(jobIds, $"{jobIds.Count} job(s) queued."));
    }

    private static IResult ListJobs(JobManager jobs) =>
        Results.Ok(jobs.Snapshot().Select(JobView.From).ToArray());

    private static IResult GetStatus(string jobId, JobManager jobs)
    {
        var record = jobs.Find(jobId);
        return record is null
            ? Problem(StatusCodes.Status404NotFound, "Job not found")
            : Results.Ok(JobView.From(record));
    }

    private static IResult Cancel(string jobId, JobManager jobs) => jobs.Cancel(jobId) switch
    {
        JobCancelOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "Job not found"),
        JobCancelOutcome.Cancelled => Results.Ok(new CancelResponse(true)),

        // Already finished. Not an error: the UI reuses DELETE as "remove row".
        _ => Results.Ok(new CancelResponse(false)),
    };

    private static IResult Download(string jobId, JobManager jobs)
    {
        var record = jobs.Find(jobId);
        if (record is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Job not found");
        }

        var path = record.Request.OutputPath;
        return File.Exists(path)
            ? Results.File(path, "application/octet-stream", Path.GetFileName(path))
            : Problem(StatusCodes.Status404NotFound, "Output not ready");
    }

    /// <summary>Zips the finished outputs the user ticked.</summary>
    /// <remarks>
    /// <para>
    /// Written straight to the response body. The Python built a temp zip,
    /// returned it, and deleted it from a background callback; streaming it
    /// removes the scratch file, the cleanup, and any chance of two concurrent
    /// downloads colliding, and holds one entry in memory rather than the whole
    /// archive - which matters when the entries are audiobooks.
    /// </para>
    /// <para>
    /// Entries are stored rather than deflated: these are already-compressed
    /// media files, so compressing again spends CPU to save nothing.
    /// </para>
    /// </remarks>
    private static IResult DownloadZip(string? ids, JobManager jobs, HttpContext context)
    {
        var wanted = (ids ?? string.Empty).Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var paths = new List<string>(wanted.Length);
        foreach (var jobId in wanted)
        {
            var record = jobs.Find(jobId);

            // The UI lets a row be ticked before it finishes, so anything
            // without a readable output is skipped rather than failing the lot.
            if (record?.Status == JobStatus.Completed && File.Exists(record.Request.OutputPath))
            {
                paths.Add(record.Request.OutputPath);
            }
        }

        if (paths.Count == 0)
        {
            return Problem(StatusCodes.Status404NotFound, "No completed outputs to zip");
        }

        // ZipArchive has no asynchronous write path - it flushes entry headers
        // and the central directory synchronously - and the response body
        // rejects synchronous writes by default. The alternatives are buffering
        // the whole archive in memory, which defeats the point when the entries
        // are audiobooks, or going back to a scratch file. Allowing synchronous
        // writes on this one response is the narrowest of the three.
        var bodyControl = context.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        return Results.Stream(
            async stream =>
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var path in paths)
                {
                    // Finding 3: the output file is already named
                    // censored_<name>. The Python added another "censored_" here
                    // and every archive arrived full of censored_censored_*.
                    var entry = archive.CreateEntry(
                        Path.GetFileName(path), CompressionLevel.NoCompression);

                    await using var source = File.OpenRead(path);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target);
                }
            },
            "application/zip",
            "foulfilter_results.zip");
    }

    private static void Discard(UploadRequest upload)
    {
        foreach (var file in upload.Files)
        {
            try
            {
                File.Delete(file.InputPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IResult Problem(int statusCode, string detail) =>
        Results.Json(new ErrorResponse(detail), statusCode: statusCode);
}
