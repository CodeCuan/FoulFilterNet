using System.CommandLine;
using System.Globalization;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net.LibraryLoader;

namespace FoulFilterNet.Evaluation;

/// <summary>
/// <c>foulfilter-eval</c>: every fixture in the manifest through the real
/// pipeline, scored as raw words and as final hits, printed as a table and
/// written as JSON for a later run to diff against.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var manifest = new Option<FileInfo?>("--manifest")
        {
            Description = "Ground truth manifest (default: tests/fixtures/media/manifest.json above the working directory)",
        };
        var work = new Option<DirectoryInfo?>("--work")
        {
            Description = "Scratch directory for transcripts and outputs (default: a new folder under the system temp directory)",
        };
        var json = new Option<FileInfo?>("--json") { Description = "Where to write the JSON report (default: <work>/evaluation.json)" };
        var badWords = new Option<FileInfo?>("--bad-words")
        {
            Description = "Bad Words List to match with (default: exactly the phrases the manifest planted)",
        };
        var innocent = new Option<string[]>("--innocent")
        {
            Description = "Fixture whose spans are innocent words planted to be flagged wrongly (repeatable; default false_positive.mp3)",
            AllowMultipleArgumentsPerToken = true,
        };
        var transcripts = new Option<DirectoryInfo?>("--transcripts")
        {
            Description = "Transcript cache to use; an existing one is resumed rather than re-transcribed (default: <work>/transcripts)",
        };
        var rescan = new Option<bool>("--rescan") { Description = "Run the Rescan Pass as a job would with --rescan" };

        var command = new RootCommand("Score the pipeline against the fixture manifest: detection, recall, precision and signed boundary error.")
        {
            manifest, work, json, badWords, innocent, transcripts, rescan,
        };

        command.SetAction((parsed, cancellationToken) => RunAsync(
            new Arguments(
                parsed.GetValue(manifest)?.FullName,
                parsed.GetValue(work)?.FullName,
                parsed.GetValue(json)?.FullName,
                parsed.GetValue(badWords)?.FullName,
                parsed.GetValue(innocent) is { Length: > 0 } named ? named : null,
                parsed.GetValue(transcripts)?.FullName,
                parsed.GetValue(rescan)),
            cancellationToken));

        return await command.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = true });
    }

    private static async Task<int> RunAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddLegacyEnvironmentVariables();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddEvaluationPipeline(builder.Configuration);

        using var host = builder.Build();

        var manifestPath = arguments.Manifest ?? GroundTruthManifest.Locate(Directory.GetCurrentDirectory());
        var fixtures = GroundTruthManifest.Load(manifestPath, arguments.Innocent ?? GroundTruthManifest.DefaultInnocentFixtures);

        var workDirectory = arguments.Work ?? Path.Combine(
            Path.GetTempPath(), "foulfilter-eval", started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workDirectory);

        var badWordsPath = arguments.BadWords;
        if (badWordsPath is null)
        {
            badWordsPath = Path.Combine(workDirectory, "bad_words.txt");
            await File.WriteAllLinesAsync(badWordsPath, GroundTruthManifest.Phrases(fixtures), cancellationToken);
        }

        var settings = new EvaluationSettings
        {
            MediaDirectory = Path.GetDirectoryName(manifestPath)!,
            BadWordsPath = badWordsPath,
            WorkDirectory = workDirectory,
            TranscriptDirectory = arguments.Transcripts ?? Path.Combine(workDirectory, "transcripts"),
            Rescan = arguments.Rescan,
        };

        var transcription = host.Services.GetRequiredService<IOptions<TranscriptionOptions>>().Value;
        var smartCut = host.Services.GetRequiredService<ISmartCutAdvisor>();
        var evaluator = host.Services.GetRequiredService<FixtureEvaluator>();

        Console.WriteLine($"Evaluating {fixtures.Count} fixtures from {manifestPath}");
        Console.WriteLine($"Model {transcription.Model} from {Path.GetFullPath(transcription.ModelDirectory)}; work in {workDirectory}");

        var runs = new List<FixtureRun>();
        foreach (var fixture in fixtures)
        {
            var run = await evaluator.EvaluateAsync(fixture, settings, cancellationToken);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {fixture.File,-20} {run.ElapsedSeconds,7:0.000} s  {run.Words.Count,3} words{(run.UsedCachedTranscript ? "  (resumed from cache)" : string.Empty)}"));
            runs.Add(run);
        }

        clock.Stop();

        var report = EvaluationReport.Build(runs, new EvaluationRunInfo
        {
            Manifest = manifestPath,
            Model = transcription.Model,
            Language = transcription.Language,
            Device = transcription.Device.ToString(),
            Runtime = RuntimeOptions.LoadedLibrary?.ToString(),
            BadWords = badWordsPath,
            Rescan = arguments.Rescan,
            SmartCutEnabled = smartCut.IsEnabled,
            StartedAt = started.ToString("O", CultureInfo.InvariantCulture),
            TotalSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
        });

        Console.WriteLine();
        Console.Write(ReportFormatter.ToTable(report));

        var jsonPath = arguments.Json ?? Path.Combine(workDirectory, "evaluation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, ReportFormatter.ToJson(report), cancellationToken);

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Runtime {report.Run.Runtime ?? "unknown"}, {report.Run.TotalSeconds:0.0} s in total. JSON report: {jsonPath}"));

        return 0;
    }

    private sealed record Arguments(
        string? Manifest,
        string? Work,
        string? Json,
        string? BadWords,
        string[]? Innocent,
        string? Transcripts,
        bool Rescan);
}
