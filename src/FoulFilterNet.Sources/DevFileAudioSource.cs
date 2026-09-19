namespace FoulFilterNet.Sources;

/// <summary>
/// The development-only <c>file</c> provider (W16): serves a
/// <see cref="VideoRef.FileProvider"/> video from a <see cref="DevFileDirectory"/>
/// and hands every other video to the real source. It lets the whole server
/// path - fetch, prepare, windows, Hits - run on <c>tests/fixtures/media</c>
/// without YouTube.
/// </summary>
/// <remarks>
/// The Web host composes this only when <c>Watch:DevFileProvider:Enabled</c>
/// is true, which is off by default. "Fetching" a file is copying it into the
/// session's scratch directory as <c>audio.&lt;ext&gt;</c>, exactly where
/// yt-dlp would have put it, so the session deletes a copy and never the
/// fixture.
/// </remarks>
public sealed class DevFileAudioSource : IWebAudioSource
{
    private const string AudioFileStem = "audio";
    private const string NoExtension = "bin";

    private readonly IWebAudioSource _inner;
    private readonly DevFileDirectory _directory;

    /// <param name="inner">The real source, for YouTube videos and <c>/config</c>.</param>
    /// <param name="directory">Where file videos come from.</param>
    public DevFileAudioSource(IWebAudioSource inner, DevFileDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(directory);
        _inner = inner;
        _directory = directory;
    }

    /// <inheritdoc />
    public async Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(video);
        if (!string.Equals(video.Provider, VideoRef.FileProvider, StringComparison.Ordinal))
        {
            return await _inner
                .FetchAudioAsync(video, directory, cancellationToken)
                .ConfigureAwait(false);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_directory.TryResolve(video.Id, out var source))
        {
            var reason = $"There is no file named '{video.Id}' in the dev file directory.";
            throw new WebAudioException(video, WebAudioFailure.Unavailable, reason, reason);
        }

        var extension = Path.GetExtension(source).TrimStart('.');
        if (extension.Length == 0)
        {
            extension = NoExtension;
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        DeleteAudio(fullDirectory);

        var target = Path.Combine(fullDirectory, $"{AudioFileStem}.{extension}");
        try
        {
            var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true
            );
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true
                );
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return new WebAudio(
            video,
            Title: video.Id,
            DurationSeconds: null,
            AudioPath: target,
            Extension: extension,
            FormatId: null,
            Codec: null,
            JsRuntimeMissing: false
        );
    }

    /// <summary>The real source's answer: the dev provider does not make YouTube work.</summary>
    public Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    ) => _inner.CheckAvailabilityAsync(cancellationToken);

    private static void DeleteAudio(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, AudioFileStem + ".*"))
        {
            File.Delete(file);
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
