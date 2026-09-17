using System.Buffers;

namespace FoulFilterNet.Web.Uploads;

/// <summary>Raised when an upload runs past the configured size cap.</summary>
public sealed class UploadTooLargeException : Exception
{
    public UploadTooLargeException(string fileName, long maxBytes)
        : base($"{fileName} exceeds the {maxBytes / (1L << 20)} MB upload limit.")
    {
        FileName = fileName;
        MaxBytes = maxBytes;
    }

    public UploadTooLargeException()
    {
        FileName = string.Empty;
    }

    public UploadTooLargeException(string message)
        : base(message)
    {
        FileName = string.Empty;
    }

    public UploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
        FileName = string.Empty;
    }

    public string FileName { get; }

    public long MaxBytes { get; }
}

/// <summary>Streams an upload to disk without ever holding it in memory.</summary>
public static class UploadStorage
{
    private const int ChunkSize = 1 << 20;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destinationPath"/> a
    /// megabyte at a time, stopping the moment the running total passes
    /// <paramref name="maxBytes"/>. Anything that goes wrong - the cap, a client
    /// hanging up, a full disk - takes the half-written file with it, so the
    /// uploads directory never accumulates torsos.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="UploadTooLargeException">The cap was exceeded.</exception>
    public static async Task<long> SaveAsync(
        Stream source,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        long written = 0;

        try
        {
            await using (
                var destination = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    ChunkSize,
                    useAsync: true
                )
            )
            {
                int read;
                while (
                    (
                        read = await source.ReadAsync(
                            buffer.AsMemory(0, ChunkSize),
                            cancellationToken
                        )
                    ) > 0
                )
                {
                    written += read;
                    if (written > maxBytes)
                    {
                        throw new UploadTooLargeException(
                            Path.GetFileName(destinationPath),
                            maxBytes
                        );
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            return written;
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
