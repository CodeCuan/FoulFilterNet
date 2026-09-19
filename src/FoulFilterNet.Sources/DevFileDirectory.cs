using System.Diagnostics.CodeAnalysis;

namespace FoulFilterNet.Sources;

/// <summary>
/// The directory the development-only <c>file</c> provider serves (W16), and
/// the one place that turns a requested name into a path.
/// </summary>
/// <remarks>
/// <para>
/// <b>A whitelist, not a path.</b> A name resolves only if it is exactly
/// (ordinally) the name of a file directly inside the directory, as the
/// directory lists it, and is a name <see cref="VideoRef.TryCreateFile"/>
/// accepts. Nothing the caller sends is ever combined with the directory
/// until it has been found in that listing, so there is no traversal to get
/// wrong: <c>../x</c>, <c>a/b</c>, a rooted path, a leading <c>-</c> or a dot
/// file never match.
/// </para>
/// <para>
/// The directory is listed on every call rather than cached: it is a
/// development tool, and a fixture added while the host runs should just work.
/// </para>
/// </remarks>
public sealed class DevFileDirectory
{
    /// <param name="directory">The configured directory. It need not exist yet;
    /// until it does, nothing resolves.</param>
    /// <exception cref="ArgumentException">Blank.</exception>
    public DevFileDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <summary>The directory, as a full path.</summary>
    public string FullPath { get; }

    /// <summary>Whether the directory exists.</summary>
    public bool Exists => Directory.Exists(FullPath);

    /// <summary>
    /// The files that can be served: directly inside the directory, with names
    /// <see cref="VideoRef.TryCreateFile"/> accepts, in ordinal order.
    /// </summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            if (!Exists)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var path in Directory.EnumerateFiles(FullPath))
            {
                var name = Path.GetFileName(path);
                if (VideoRef.TryCreateFile(name, out _))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>
    /// The full path of the listed file called exactly <paramref name="name"/>.
    /// </summary>
    public bool TryResolve(string? name, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (!VideoRef.TryCreateFile(name, out _))
        {
            return false;
        }

        foreach (var listed in Names)
        {
            if (string.Equals(listed, name, StringComparison.Ordinal))
            {
                path = Path.Combine(FullPath, listed);
                return true;
            }
        }

        return false;
    }
}
