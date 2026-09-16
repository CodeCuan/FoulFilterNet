using Whisper.net.Ggml;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Fetches the bytes of one GGML model. Injected rather than called directly so
/// that acquisition is exercised in tests without a network, and so that a
/// deployment can refuse to acquire anything at all by supplying none.
/// </summary>
public delegate Task<Stream> GgmlWeightsSource(GgmlType model, CancellationToken cancellationToken);

/// <summary>
/// Where the weights are, and how they get there if they are not.
/// </summary>
/// <remarks>
/// <para>
/// Model weights are large (a gigabyte and a half for <c>large-v3-turbo</c>),
/// gitignored, and never committed, so an installed file is the normal case and
/// resolution has to be able to explain itself when there is none: the message
/// names the exact file and directory an operator should put it in.
/// </para>
/// <para>
/// A file that is already there is used as it is - nothing checks a hash or asks
/// Hugging Face whether a newer one exists, because a transcript cache keyed by
/// audio content (ADR-0002) assumes the engine does not change underneath it.
/// </para>
/// </remarks>
public sealed class WhisperModelSource
{
    /// <summary>Where weights live when configuration does not say.</summary>
    public const string DefaultDirectory = "models";

    private readonly GgmlWeightsSource? _acquire;

    /// <param name="directory">Where weights are kept; a relative path is resolved against the process directory.</param>
    /// <param name="acquire">How to fetch absent weights. Null means never: absent weights are then an error.</param>
    public WhisperModelSource(string directory, GgmlWeightsSource? acquire = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        DirectoryPath = Path.GetFullPath(directory);
        _acquire = acquire;
    }

    /// <summary>Whisper.net's own downloader, which fetches from Hugging Face.</summary>
    public static GgmlWeightsSource Download => (model, cancellationToken) =>
        WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, QuantizationType.NoQuantization, cancellationToken);

    /// <summary>The directory weights are read from and written to.</summary>
    public string DirectoryPath { get; }

    /// <summary>Where a model's weights would be, whether or not they are there.</summary>
    public string PathFor(string model) => Path.Combine(DirectoryPath, WhisperModelFiles.FileName(model));

    /// <summary>
    /// The path of the weights for <paramref name="model"/>, acquiring them
    /// first if they are absent and this source may.
    /// </summary>
    /// <exception cref="FileNotFoundException">The weights are absent and nothing may fetch them.</exception>
    /// <exception cref="InvalidOperationException">The weights are absent and are not a model that can be fetched.</exception>
    public async Task<string> ResolveAsync(string model, CancellationToken cancellationToken = default)
    {
        var path = PathFor(model);
        if (File.Exists(path))
        {
            return path;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fileName = WhisperModelFiles.FileName(model);
        if (_acquire is null)
        {
            throw new FileNotFoundException(
                $"No Whisper weights for '{model}'. Install '{fileName}' in '{DirectoryPath}'.", path);
        }

        var type = WhisperModelFiles.GgmlTypeFor(model)
            ?? throw new InvalidOperationException(
                $"'{model}' is not a model Whisper.net can fetch. Install '{fileName}' in '{DirectoryPath}' by hand.");

        await AcquireAsync(type, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Download into a neighbouring temporary file and move it into place, so an
    /// interrupted acquisition cannot leave a truncated model that loads and
    /// then mis-transcribes.
    /// </summary>
    private async Task AcquireAsync(GgmlType type, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);

        var partial = path + ".downloading";
        var completed = false;
        try
        {
            await using (var source = await _acquire!(type, cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(partial))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, path, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(partial);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover partial download is untidy, not fatal - and it is named
            // so that it can never be mistaken for the model itself.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
