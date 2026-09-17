using System.CommandLine;
using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Cli;

/// <summary>
/// The command line of <c>find_and_remove.py</c>, argument for argument, plus
/// the derivations the Python performed between <c>parse_args</c> and
/// <c>run_job</c>.
/// <para>
/// Parsing and mapping are deliberately separate from running anything: turning
/// arguments into a <see cref="JobRequest"/> touches neither FFmpeg nor a model,
/// which is what makes the precedence matrix testable as a unit.
/// </para>
/// </summary>
public sealed class FoulFilterCommandLine
{
    public FoulFilterCommandLine()
    {
        FilePath = new Argument<string>("file_path")
        {
            Description = "Path to the audio/video file",
        };

        BadWordsListPath = new Argument<string>("bad_words_list_path")
        {
            Description = "Path to file containing words that should be removed",
        };

        Output = new Option<string?>("--output")
        {
            Description = "Explicit path to save the final file",
        };

        Bleep = new Option<bool>("--bleep")
        {
            Description = "Bleep out words instead of replacing with silence",
        };

        Delete = new Option<bool>("--delete")
        {
            Description = "Cut words out entirely instead of silence (audio files only)",
        };

        CensorMethodName = new Option<string?>("--censor_method")
        {
            Description = "Override CENSOR_METHOD env/flags with an explicit method",
            HelpName = "method",
        };
        CensorMethodName.AcceptOnlyFromAmong(CensorMethodResolution.Accepted);

        Debug = new Option<bool>("--debug") { Description = "Export transcript to text file" };

        Rescan = new Option<bool>("--rescan")
        {
            Description =
                "Second detection pass with shifted chunk boundaries (finds boundary-garbled words)",
        };

        NoEdit = new Option<bool>("--no_edit")
        {
            Description = "Analyze only: report what would be cut, do not edit",
        };

        Command = new RootCommand(
            "Finds and removes a list of swear words from an audio or video file."
        );
        Command.Add(FilePath);
        Command.Add(BadWordsListPath);
        Command.Add(Output);
        Command.Add(Bleep);
        Command.Add(Delete);
        Command.Add(CensorMethodName);
        Command.Add(Debug);
        Command.Add(Rescan);
        Command.Add(NoEdit);
    }

    public RootCommand Command { get; }

    public Argument<string> FilePath { get; }

    public Argument<string> BadWordsListPath { get; }

    public Option<string?> Output { get; }

    public Option<bool> Bleep { get; }

    public Option<bool> Delete { get; }

    public Option<string?> CensorMethodName { get; }

    public Option<bool> Debug { get; }

    public Option<bool> Rescan { get; }

    public Option<bool> NoEdit { get; }

    public ParseResult Parse(params string[] args) => Command.Parse(args);

    /// <summary>
    /// The Python's precedence chain, whole: <c>--censor_method</c> beats
    /// <c>--bleep</c>, which beats <c>--delete</c>, which beats configuration
    /// (<see cref="ConfigurationKeys.CensorMethod"/>, which <c>CENSOR_METHOD</c>
    /// maps onto).
    /// </summary>
    /// <param name="parsed">The parsed command line.</param>
    /// <param name="configuration">A configuration lookup by key.</param>
    public CensorMethod ResolveCensorMethod(ParseResult parsed, Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(configuration);

        return CensorMethodResolution.Resolve(
            parsed.GetValue(CensorMethodName),
            parsed.GetValue(Bleep),
            parsed.GetValue(Delete),
            configuration(ConfigurationKeys.CensorMethod)
        );
    }

    /// <summary>
    /// Everything <c>run_job</c> was handed: the derived output path, the
    /// scratch directory beside it, and the transcript cache - which is Web's
    /// cache, resolved from the same <c>Storage</c> keys, so a file transcribed
    /// by either host is a Resume hit for the other.
    /// </summary>
    /// <param name="parsed">The parsed command line.</param>
    /// <param name="configuration">A configuration lookup by key.</param>
    public JobRequest ToRequest(ParseResult parsed, Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(configuration);

        var inputPath = parsed.GetRequiredValue(FilePath);
        var baseName = Path.GetFileNameWithoutExtension(inputPath);

        // The scratch directory takes its name from the input even when --output
        // moves it elsewhere, exactly as the Python composed it.
        var outputPath = parsed.GetValue(Output) is { Length: > 0 } explicitOutput
            ? explicitOutput
            : Path.Combine(
                DirectoryOf(inputPath),
                $"censored_{baseName}{Path.GetExtension(inputPath)}"
            );

        return new JobRequest
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            BadWordsPath = parsed.GetRequiredValue(BadWordsListPath),
            TranscriptDirectory = DataLocations.TranscriptDirectory(
                configuration(ConfigurationKeys.DataDirectory),
                configuration(ConfigurationKeys.TranscriptDirectory)
            ),
            ScratchDirectory = Path.Combine(DirectoryOf(outputPath), $".{baseName}_scratch"),
            CensorMethod = ResolveCensorMethod(parsed, configuration),
            Debug = parsed.GetValue(Debug),
            Rescan = parsed.GetValue(Rescan),

            // --no_edit is Render turned off rather than a parallel concept.
            Render = !parsed.GetValue(NoEdit),
        };
    }

    /// <summary>The Python's <c>os.path.dirname(path) or "."</c>.</summary>
    private static string DirectoryOf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }
}

/// <summary>
/// How the four sources of a Censor Method are reconciled, and the one spelling
/// the legacy deployment used that the enum does not have.
/// </summary>
public static class CensorMethodResolution
{
    /// <summary>
    /// What <c>--censor_method</c> accepts. <c>delete</c> is not one of the
    /// enum's names: the Python's <c>choices</c> list excluded it while its
    /// environment variable accepted it, so the flag and the variable disagreed
    /// about a word that appears in every legacy deployment's <c>.env</c>.
    /// </summary>
    public static readonly string[] Accepted = ["silence", "bleep", "remove", "delete"];

    /// <summary>The whole chain, in the Python's order.</summary>
    public static CensorMethod Resolve(
        string? explicitMethod,
        bool bleep,
        bool delete,
        string? environmentDefault
    )
    {
        if (TryParse(explicitMethod, out var chosen))
        {
            return chosen;
        }

        if (bleep)
        {
            return CensorMethod.Bleep;
        }

        if (delete)
        {
            return CensorMethod.Remove;
        }

        // An unset or unrecognised variable is silence, which is what
        // os.getenv("CENSOR_METHOD", "silence") plus an unknown value did: the
        // pipeline treated anything it did not know as silence rather than
        // failing a run over a stale variable.
        return TryParse(environmentDefault, out var fromEnvironment)
            ? fromEnvironment
            : CensorMethod.Silence;
    }

    /// <summary>
    /// Case-insensitive, whitespace-tolerant, and <c>delete</c> means
    /// <see cref="CensorMethod.Remove"/>.
    /// </summary>
    public static bool TryParse(string? value, out CensorMethod method)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "silence":
                method = CensorMethod.Silence;
                return true;
            case "bleep":
                method = CensorMethod.Bleep;
                return true;
            case "remove":
            case "delete":
                method = CensorMethod.Remove;
                return true;
            default:
                method = CensorMethod.Silence;
                return false;
        }
    }
}
