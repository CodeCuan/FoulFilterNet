using FoulFilterNet.Media;

namespace FoulFilterNet.Sources.Tests;

/// <summary>
/// An <see cref="IProcessRunner"/> that starts nothing. Each executable name is
/// given a handler that stands in for the real process - writing files into the
/// scratch directory, returning captured output, or waiting to be cancelled.
/// An executable with no handler behaves as one that is not installed, which
/// is exactly what the real runner reports for a name it cannot start.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<
        string,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>
    > _handlers = new(StringComparer.Ordinal);

    private readonly List<(string FileName, IReadOnlyList<string> Arguments)> _calls = [];

    /// <summary>Every invocation, oldest first.</summary>
    public IReadOnlyList<(string FileName, IReadOnlyList<string> Arguments)> Calls => _calls;

    /// <summary>The token the most recent invocation was given.</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Handle every run of <paramref name="fileName"/>.</summary>
    public FakeProcessRunner On(
        string fileName,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> handler
    )
    {
        _handlers[fileName] = handler;
        return this;
    }

    /// <summary>Answer every run of <paramref name="fileName"/> with a fixed result.</summary>
    public FakeProcessRunner On(
        string fileName,
        int exitCode,
        string standardOutput = "",
        string standardError = ""
    ) =>
        On(
            fileName,
            (_, _) => Task.FromResult(new ProcessResult(exitCode, standardOutput, standardError))
        );

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default
    )
    {
        _calls.Add((fileName, [.. arguments]));
        LastToken = cancellationToken;

        if (!_handlers.TryGetValue(fileName, out var handler))
        {
            throw new ProcessNotStartedException(
                fileName,
                new System.ComponentModel.Win32Exception(
                    2,
                    "The system cannot find the file specified."
                )
            );
        }

        return handler(arguments, cancellationToken);
    }
}

/// <summary>
/// Stand-ins for what yt-dlp does to the scratch directory, driven by the
/// arguments it was given, so the fake cannot write somewhere the real one
/// would not.
/// </summary>
internal static class FakeYtDlp
{
    /// <summary>The directory from <c>--paths</c>.</summary>
    public static string DirectoryOf(IReadOnlyList<string> arguments) =>
        arguments[arguments.ToList().IndexOf("--paths") + 1];

    /// <summary>
    /// A clean download: write <c>audio.webm</c> and print the captured success
    /// output pointing at it.
    /// </summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> Downloads(
        string output = YtDlpSamples.SuccessOutput,
        string standardError = "",
        string fileName = "audio.webm"
    ) =>
        (arguments, _) =>
        {
            var path = Path.Combine(DirectoryOf(arguments), fileName);
            File.WriteAllBytes(path, [1, 2, 3]);
            return Task.FromResult(
                new ProcessResult(0, YtDlpSamples.WithAudioPath(output, path), standardError)
            );
        };

    /// <summary>
    /// A download that leaves a partial file behind and then fails as the
    /// captured sample did.
    /// </summary>
    public static Func<
        IReadOnlyList<string>,
        CancellationToken,
        Task<ProcessResult>
    > FailsAfterAPartialFile(int exitCode, string standardError, string standardOutput = "") =>
        (arguments, _) =>
        {
            File.WriteAllBytes(Path.Combine(DirectoryOf(arguments), "audio.webm.part"), [1]);
            return Task.FromResult(new ProcessResult(exitCode, standardOutput, standardError));
        };

    /// <summary>
    /// A download in progress: the partial file exists, and the process runs
    /// until it is cancelled, as the real runner does when it kills the tree.
    /// </summary>
    public static Func<
        IReadOnlyList<string>,
        CancellationToken,
        Task<ProcessResult>
    > RunsUntilCancelled(TaskCompletionSource started) =>
        async (arguments, cancellationToken) =>
        {
            var directory = DirectoryOf(arguments);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "audio.webm.part"),
                [1, 2],
                cancellationToken
            );
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "audio.webm.ytdl"),
                [3],
                cancellationToken
            );
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
}

/// <summary>A directory that deletes itself.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ffn_sources_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

    public IReadOnlyList<string> FileNames() =>
        Directory.Exists(Path)
            ?
            [
                .. Directory
                    .GetFiles(Path)
                    .Select(System.IO.Path.GetFileName)
                    .Order(StringComparer.Ordinal)!,
            ]
            : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }
}
