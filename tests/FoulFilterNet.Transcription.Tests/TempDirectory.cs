namespace FoulFilterNet.Transcription.Tests;

/// <summary>A directory that lives as long as the test that made it.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ffn_t13_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is untidy, not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
