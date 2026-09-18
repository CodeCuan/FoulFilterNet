using System.Buffers.Binary;
using System.Text;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// Writes small PCM WAV files for tests that need a real file on disk but no
/// FFmpeg: the header is the canonical 44 bytes, optionally with a
/// <c>LIST</c> chunk in front of the data the way FFmpeg writes one.
/// </summary>
internal static class TestWav
{
    /// <summary>
    /// A 16-bit file whose every channel of frame <c>f</c> holds
    /// <paramref name="sample"/>(f, channel).
    /// </summary>
    public static void Write(
        string path,
        long frames,
        Func<long, int, short> sample,
        int sampleRate = 16000,
        int channels = 1,
        bool withListChunk = false
    )
    {
        var data = new byte[frames * channels * sizeof(short)];
        for (long frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    data.AsSpan((int)((frame * channels + channel) * sizeof(short))),
                    sample(frame, channel)
                );
            }
        }

        WriteRaw(path, data, sampleRate, channels, bitsPerSample: 16, withListChunk);
    }

    /// <summary>A file of <paramref name="data"/> bytes under any header.</summary>
    public static void WriteRaw(
        string path,
        byte[] data,
        int sampleRate,
        int channels,
        int bitsPerSample,
        bool withListChunk = false
    )
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        var list = withListChunk ? ListChunk() : [];
        var blockAlign = channels * bitsPerSample / 8;

        writer.Write("RIFF"u8);
        writer.Write(4 + (8 + 16) + list.Length + (8 + data.Length));
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(list);

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
    }

    /// <summary>
    /// The <c>LIST/INFO/ISFT</c> chunk FFmpeg puts between <c>fmt </c> and
    /// <c>data</c>, so the data chunk does not start at byte 44.
    /// </summary>
    private static byte[] ListChunk()
    {
        var software = "Lavf61.7.100\0"u8.ToArray();
        var padded = software.Length % 2 == 0 ? software : [.. software, 0];

        using var chunk = new MemoryStream();
        using var writer = new BinaryWriter(chunk);
        writer.Write("LIST"u8);
        writer.Write(4 + 8 + padded.Length);
        writer.Write("INFO"u8);
        writer.Write("ISFT"u8);
        writer.Write(software.Length);
        writer.Write(padded);
        writer.Flush();
        return chunk.ToArray();
    }
}
