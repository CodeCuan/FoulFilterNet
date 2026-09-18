using Microsoft.Win32.SafeHandles;
using Whisper.net.Wave;

namespace FoulFilterNet.Transcription;

/// <summary>
/// A 16-bit PCM analysis WAV opened once, planned into
/// <see cref="TranscriptionWindows"/>, and read one window at a time as mono
/// samples - the I/O half of transcribing a window, with no model involved.
/// </summary>
/// <remarks>
/// <para>
/// This is what <see cref="WhisperNetEngine"/> did inline before a Watch
/// Session needed windows in its own order, moved out unchanged: the header is
/// parsed by Whisper.net's own <see cref="WaveParser"/> (so a <c>LIST</c>
/// chunk from FFmpeg is skipped the same way), the duration is the header's
/// frame count over the sample rate, a window's frames are its start and end
/// rounded to the nearest frame, and channels are averaged into samples in
/// [-1, 1) the way Whisper.net's parser does. A file cut short reads what is
/// there rather than failing.
/// </para>
/// <para>
/// Reads go through a positionless file handle, so windows can be read
/// concurrently and in any order without sharing a stream position. The file
/// stays open (shared for reading) until this is disposed, which a caller
/// that deletes the WAV afterwards has to do first on Windows.
/// </para>
/// </remarks>
public sealed class AnalysisWav : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly long _dataPosition;
    private readonly int _channels;
    private readonly long _frameCount;
    private readonly int _sampleRate;

    private AnalysisWav(
        string path,
        SafeFileHandle file,
        long dataPosition,
        int channels,
        int sampleRate,
        long frameCount
    )
    {
        Path = path;
        _file = file;
        _dataPosition = dataPosition;
        _channels = channels;
        _sampleRate = sampleRate;
        _frameCount = frameCount;
        DurationSeconds = (double)frameCount / sampleRate;
        Windows = TranscriptionWindows.Plan(DurationSeconds);
    }

    /// <summary>The file this reads.</summary>
    public string Path { get; }

    /// <summary>The audio's length, from the header's frame count.</summary>
    public double DurationSeconds { get; }

    /// <summary>
    /// <see cref="TranscriptionWindows.Plan"/> of <see cref="DurationSeconds"/>:
    /// the same windows the batch path has always used.
    /// </summary>
    public IReadOnlyList<TranscriptionWindow> Windows { get; }

    /// <summary>Open and parse the header. The file is only read, never written.</summary>
    /// <exception cref="NotSupportedException">The WAV is not 16-bit PCM.</exception>
    public static async Task<AnalysisWav> OpenAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        // The parser wants a stream; it reads the header and is closed. The
        // audio is then read through a handle of its own, which has no
        // position to share between concurrent reads.
        WaveParser wave;
        await using (var header = File.OpenRead(path))
        {
            wave = new WaveParser(header);
            await wave.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (wave.BitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"Expected 16-bit PCM in '{path}', found {wave.BitsPerSample}-bit."
            );
        }

        var file = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess
        );

        return new AnalysisWav(
            path,
            file,
            (long)wave.DataChunkPosition,
            wave.Channels,
            (int)wave.SampleRate,
            (long)wave.DataChunkSize / (wave.Channels * sizeof(short))
        );
    }

    /// <summary>
    /// Window <paramref name="index"/> of <see cref="Windows"/> as mono samples.
    /// Safe to call concurrently.
    /// </summary>
    public async Task<float[]> ReadWindowAsync(
        int index,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Windows.Count);
        ObjectDisposedException.ThrowIf(_file.IsClosed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var window = Windows[index];
        var frameBytes = _channels * sizeof(short);
        var firstFrame = (long)Math.Round(window.Start * _sampleRate);
        var frames = (int)(
            Math.Min(_frameCount, (long)Math.Round(window.End * _sampleRate)) - firstFrame
        );

        var bytes = new byte[frames * frameBytes];
        var position = _dataPosition + firstFrame * frameBytes;
        var read = 0;
        while (read < bytes.Length)
        {
            var got = await RandomAccess
                .ReadAsync(_file, bytes.AsMemory(read), position + read, cancellationToken)
                .ConfigureAwait(false);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        var samples = new float[read / frameBytes];
        for (var frame = 0; frame < samples.Length; frame++)
        {
            var sum = 0;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += BitConverter.ToInt16(bytes, (frame * _channels + channel) * sizeof(short));
            }

            samples[frame] = sum / (_channels * 32768f);
        }

        return samples;
    }

    /// <summary>Close the file. Idempotent.</summary>
    public void Dispose() => _file.Dispose();
}
