using FoulFilterNet.Domain;

namespace FoulFilterNet.Watch;

/// <summary>
/// The Bad Words List read from its file, and read again only when the file's
/// last-write time or length changes - the same file every Job reads
/// (<c>Storage:BadWordsPath</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Change detection is a stat, not a read.</b> Every heartbeat of every
/// watched video asks for the list, so the common case - nothing changed -
/// costs one file-system metadata lookup. Last-write time alone can miss an
/// edit saved twice within the file system's timestamp resolution; the length
/// catches most of those, and the next edit catches the rest.
/// </para>
/// <para>
/// <b>A bad edit keeps the last good list.</b> An editor saving the file can
/// leave it empty or briefly locked, and a list with no usable line is
/// rejected by <see cref="BadWordsList.FromLines"/>. Neither should switch the
/// censoring off under a viewer, so once a list has been read, any failure to
/// read a newer one leaves the previous list in force, and the file is read again
/// on every ask until it reads cleanly. Only when no list has ever been read does
/// <see cref="GetCurrent"/> throw.
/// </para>
/// </remarks>
public sealed class FileBadWordsSource : IBadWordsSource
{
    private readonly Lock _gate = new();
    private readonly string _path;

    private BadWordsList? _list;
    private (DateTime WrittenUtc, long Length)? _stamp;

    /// <summary>Read the list from <paramref name="path"/> when first asked, and again whenever it changes.</summary>
    public FileBadWordsSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The file the list is read from.</summary>
    public string Path => _path;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The file is missing, unreadable or has no usable line, and no earlier
    /// read succeeded.
    /// </exception>
    public BadWordsList GetCurrent()
    {
        lock (_gate)
        {
            var stamp = TryStamp();
            if (_list is not null && (stamp is null || stamp == _stamp))
            {
                return _list;
            }

            try
            {
                _list = BadWordsList.FromLines(File.ReadAllLines(_path));
                _stamp = stamp;
                return _list;
            }
            catch (Exception exception) when (IsUnreadable(exception))
            {
                if (_list is not null)
                {
                    // Keep the last good list. The stamp is left as it was, so
                    // the next ask reads the file again.
                    return _list;
                }

                throw new InvalidOperationException(
                    $"The Bad Words List could not be read from {_path}: {exception.Message}",
                    exception
                );
            }
        }
    }

    private (DateTime, long)? TryStamp()
    {
        try
        {
            var file = new FileInfo(_path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            return null;
        }
    }

    private static bool IsUnreadable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException;
}
