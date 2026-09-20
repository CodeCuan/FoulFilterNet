using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FoulFilterNet.Transcription;

/// <summary>
/// A last-breath trace: one line per step, on disk before the next step runs.
/// </summary>
/// <remarks>
/// <para>
/// whisper.cpp can end the process outright rather than throwing - a native
/// fast-fail, which Windows reports as exit code <c>0xC0000409</c>
/// (<c>STATUS_STACK_BUFFER_OVERRUN</c>). Nothing managed runs afterwards: no
/// exception, no <c>finally</c>, no flush of a buffered log. So the last thing
/// the log shows is whatever had already reached the console, which is rarely
/// the step that died.
/// </para>
/// <para>
/// This writes each line straight through to the file, so the last line in the
/// file is the last thing the process did. It costs a synchronous disk write
/// per line, which is why it is off unless a path is configured - and why it
/// traces steps (a window, an open, a release), never words or samples.
/// </para>
/// <para>
/// It is deliberately static and independent of <c>ILogger</c>: the point is to
/// survive a process that is about to die, without a provider, a scope or a
/// background writer between the call and the disk.
/// </para>
/// </remarks>
public static class CrashTrace
{
    private static readonly Lock Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private static FileStream? _file;

    /// <summary>Whether a path was configured and could be opened.</summary>
    /// <remarks>
    /// Checked at the call site before building a detail string, so tracing
    /// costs nothing at all when it is off.
    /// </remarks>
    public static bool IsEnabled => Volatile.Read(ref _file) is not null;

    /// <summary>Where lines are being written, or null when tracing is off.</summary>
    public static string? Path { get; private set; }

    /// <summary>
    /// Start tracing to <paramref name="path"/>, appending to whatever is
    /// there so a crash and the run after it sit in one file.
    /// </summary>
    /// <returns>
    /// True when tracing is on. A path that cannot be opened returns false
    /// rather than throwing: a diagnostic must never be the reason a run fails.
    /// </returns>
    public static bool Start(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (Gate)
        {
            Stop();

            try
            {
                var full = System.IO.Path.GetFullPath(path);
                var directory = System.IO.Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _file = new FileStream(
                    full,
                    FileMode.Append,
                    FileAccess.Write,
                    // Shared for reading so the file can be tailed while the
                    // process it is tracing is still running.
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.WriteThrough
                );

                Path = full;
            }
            catch (Exception exception) when (exception is IOException or SystemException)
            {
                _file = null;
                Path = null;
                return false;
            }
        }

        var process = Process.GetCurrentProcess();
        Write(
            "trace.open",
            $"pid={process.Id} machine={Environment.MachineName} runtime={Environment.Version} os={Environment.OSVersion.VersionString}"
        );

        return true;
    }

    /// <summary>Write one line, and put it on the disk before returning.</summary>
    /// <param name="step">
    /// A short dotted name for the step, such as <c>window.native.begin</c>, so
    /// the file can be read by eye and filtered with a search.
    /// </param>
    /// <param name="detail">Whatever would identify this step in a crash.</param>
    public static void Write(string step, string detail = "")
    {
        var file = Volatile.Read(ref _file);
        if (file is null)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:HH:mm:ss.fff} +{Uptime.Elapsed.TotalSeconds, 9:F3}s t{Environment.CurrentManagedThreadId:D3} {step} {detail}\n"
        );

        var bytes = Encoding.UTF8.GetBytes(line);

        lock (Gate)
        {
            if (_file is not { } open)
            {
                return;
            }

            try
            {
                open.Write(bytes, 0, bytes.Length);

                // Through the operating system's own cache as well: a
                // fast-fail does not unwind, and a line still in a buffer is a
                // line that will not be in the file afterwards.
                open.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                // A full or disconnected disk must not take the process with
                // it - the trace is here to explain a crash, not to cause one.
            }
            catch (ObjectDisposedException)
            {
                // Stopped from another thread between the read and the lock.
            }
        }
    }

    /// <summary>Stop tracing and close the file. Idempotent.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
            Path = null;
        }
    }
}
