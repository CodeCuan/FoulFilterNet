using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FoulFilterNet.Web.Uploads;

/// <summary>One file that made it onto disk, and the job id its paths were built from.</summary>
public sealed record UploadedFile(string JobId, string FileName, string InputPath);

/// <summary>Everything <c>POST /upload</c> carried.</summary>
public sealed record UploadRequest(
    IReadOnlyList<UploadedFile> Files,
    IReadOnlyDictionary<string, string> Fields
)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Checkbox fields arrive as the strings JavaScript's <c>String(bool)</c>
    /// produces, so anything that is not exactly "true" is false.
    /// </summary>
    public bool Flag(string name) =>
        string.Equals(Field(name), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raised when an upload names a file the pipeline cannot open.</summary>
public sealed class UnsupportedUploadException : Exception
{
    public UnsupportedUploadException(string fileName)
        : base($"Unsupported file type: {fileName}")
    {
        FileName = fileName;
    }

    public UnsupportedUploadException()
        : this(string.Empty) { }

    public UnsupportedUploadException(string message, Exception innerException)
        : base(message, innerException)
    {
        FileName = string.Empty;
    }

    public string FileName { get; } = string.Empty;
}

/// <summary>
/// Reads a multipart upload a section at a time.
/// <para>
/// Deliberately not <c>HttpRequest.Form</c>: that buffers the whole body before
/// the handler sees any of it, which for a 4 GB audiobook means either an
/// enormous temporary file or a rejection by a limit that has nothing to do with
/// the configured one. Streaming sections keeps the size cap the only thing
/// deciding how much disk an upload can take.
/// </para>
/// </summary>
public static class UploadRequestReader
{
    public static async Task<UploadRequest> ReadAsync(
        HttpRequest request,
        string uploadDirectory,
        long maxBytes,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        // Our own cap is the one that matters; the server-wide default is sized
        // for forms, not for media.
        var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = null;
        }

        var boundary = HeaderUtilities
            .RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary)
            .Value;

        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidDataException("The upload is missing its multipart boundary.");
        }

        var reader = new MultipartReader(boundary, request.Body);
        var files = new List<UploadedFile>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            for (
                var section = await reader.ReadNextSectionAsync(cancellationToken);
                section is not null;
                section = await reader.ReadNextSectionAsync(cancellationToken)
            )
            {
                if (
                    !ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out var disposition
                    )
                )
                {
                    continue;
                }

                if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                {
                    files.Add(
                        await SaveAsync(
                            section,
                            disposition,
                            uploadDirectory,
                            maxBytes,
                            cancellationToken
                        )
                    );
                }
                else if (disposition.Name.HasValue)
                {
                    using var text = new StreamReader(section.Body);
                    fields[disposition.Name.Value!] = await text.ReadToEndAsync(cancellationToken);
                }
            }
        }
        catch
        {
            // A rejected upload must not leave the files that preceded it
            // behind - the Python returned its 400 and left them on disk.
            foreach (var saved in files)
            {
                TryDelete(saved.InputPath);
            }

            throw;
        }

        return new UploadRequest(files, fields);
    }

    private static async Task<UploadedFile> SaveAsync(
        MultipartSection section,
        ContentDispositionHeaderValue disposition,
        string uploadDirectory,
        long maxBytes,
        CancellationToken cancellationToken
    )
    {
        var raw = disposition.FileNameStar.HasValue
            ? disposition.FileNameStar.Value
            : disposition.FileName.Value;

        var fileName = UploadFileName.Sanitize(raw);
        if (!UploadFileName.IsAllowedExtension(fileName))
        {
            throw new UnsupportedUploadException(fileName);
        }

        var jobId = Jobs.JobId.New();
        var inputPath = Path.Combine(uploadDirectory, $"{jobId}__{fileName}");

        await UploadStorage.SaveAsync(section.Body, inputPath, maxBytes, cancellationToken);

        return new UploadedFile(jobId, fileName, inputPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
