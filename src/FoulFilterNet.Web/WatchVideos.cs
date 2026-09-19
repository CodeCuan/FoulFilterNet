using System.Diagnostics.CodeAnalysis;
using FoulFilterNet.Sources;

namespace FoulFilterNet.Web;

/// <summary>
/// Which videos the Watch endpoints accept: YouTube always, through
/// <see cref="VideoRef.TryCreate"/>, and the development-only <c>file</c>
/// provider only when the host switched it on (W16).
/// </summary>
/// <param name="allowFiles">The dev file provider is on.</param>
public sealed class WatchVideos(bool allowFiles)
{
    /// <summary>The dev file provider is on.</summary>
    public bool AllowsFiles { get; } = allowFiles;

    /// <summary>Validate a provider and ID from the network. Never throws.</summary>
    public bool TryRead(string? provider, string? id, [NotNullWhen(true)] out VideoRef? video)
    {
        if (
            AllowsFiles
            && string.Equals(provider, VideoRef.FileProvider, StringComparison.OrdinalIgnoreCase)
        )
        {
            return VideoRef.TryCreateFile(id, out video);
        }

        return VideoRef.TryCreate(provider, id, out video);
    }
}
