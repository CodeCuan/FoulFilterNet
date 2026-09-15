using System.Diagnostics;

namespace FoulFilterNet.Media;

/// <summary>
/// The pure half of the process adapter: turning an executable name and an
/// argument list into a <see cref="ProcessStartInfo"/>, and rendering the same
/// pair as a log line. Kept separate from <see cref="FFmpegRunner"/> so both can
/// be asserted without starting anything.
/// </summary>
public static class FFmpegProcess
{
    /// <summary>
    /// Configure a launch. Arguments go into <see cref="ProcessStartInfo.ArgumentList"/>
    /// rather than a single string, so paths with spaces need no quoting and no
    /// filtergraph is ever mangled by the shell.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Render a command the way a human would type it, quoting only what needs
    /// it. For logs and failure messages; never fed back to a shell.
    /// </summary>
    public static string Describe(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', new[] { fileName }.Concat(arguments.Select(Quote)));
    }

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Contains(' ', StringComparison.Ordinal)
            ? $"\"{argument}\""
            : argument;
}

/// <summary>Where the FFmpeg binaries live.</summary>
public sealed class FFmpegOptions
{
    /// <summary>Path to the FFmpeg executable. Resolved from <c>PATH</c> by default.</summary>
    public string FFmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to the FFprobe executable. Resolved from <c>PATH</c> by default.</summary>
    public string FFprobePath { get; set; } = "ffprobe";
}

/// <summary>Shared argument fragments, built fresh so callers can append to them.</summary>
public static class FFmpegArguments
{
    /// <summary>
    /// The prefix every invocation carries: overwrite the output without
    /// prompting, and log nothing but errors.
    /// </summary>
    /// <param name="rest">Arguments appended after the prefix.</param>
    public static IReadOnlyList<string> Quiet(params string[] rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        var arguments = new List<string>(3 + rest.Length) { "-y", "-loglevel", "error" };
        arguments.AddRange(rest);
        return arguments;
    }
}
