using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Jobs;
using FoulFilterNet.Media;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;
using FoulFilterNet.Web;
using FoulFilterNet.Web.Endpoints;
using Microsoft.Extensions.Options;

// Composition root.
var builder = WebApplication.CreateBuilder(args);

// appsettings.json is the primary source; the Python's variable names
// (DATA_DIR, WHISPER_MODEL, AI_ENHANCE, ...) override it through the translator
// the CLI shares. A Section__Key variable for the same setting still wins.
builder.Configuration.AddLegacyEnvironmentVariables();

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName)
);
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection("Transcription"));

// Binds the SmartCut section, registers the named client, and resolves exactly
// one ISmartCutAdvisor - the no-op when the feature is off or unconfigured. The
// pipeline therefore never asks whether the feature is on.
builder.Services.AddSmartCut(builder.Configuration);

// The front end and the Python it was written against both speak snake_case.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
);

builder.Services.AddSingleton<JobEventFanOut>();
builder.Services.AddSingleton<JobManager>();

// Registered before the worker: hosted services start in registration order, so
// the directories exist and last run's orphans are gone before a job can run.
builder.Services.AddHostedService<StorageHousekeeping>();
builder.Services.AddHostedService<JobWorker>();

// The engines the orchestrator composes. Every one of them is an adapter over
// something outside the process - FFmpeg, the GPU, an LLM - which is why they
// are interfaces and why none of them appears in the pipeline's own tests.
builder.Services.AddSingleton<IFFmpegRunner, FFmpegRunner>();
builder.Services.AddSingleton<IMediaProber, FFprobeMediaProber>();
builder.Services.AddSingleton<IAudioPreparer, FFmpegAudioPreparer>();
builder.Services.AddSingleton<IMediaEditor, MediaEditor>();

// Speech to text is whisper.cpp through Whisper.net, on CUDA where there is a
// card and on the CPU where there is not (ADR-0006). The engine loads its model
// on the first transcription rather than here, so building the host touches
// no GPU, no native library and no weights file - and a job that resumed a
// cached transcript releases a model it never loaded without complaint.
builder.Services.AddSingleton<IWhisperEngine>(provider => new WhisperNetEngine(
    provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value,
    new WhisperModelSource(
        provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value.ModelDirectory,
        WhisperModelSource.Download
    ),
    provider.GetRequiredService<ILogger<WhisperNetEngine>>()
));

// Conversion, padding, rescan rebasing and temporary-file cleanup sit around the
// engine rather than inside it, which is what keeps them testable without a GPU.
builder.Services.AddSingleton<WhisperTranscriber>(provider => new WhisperTranscriber(
    provider.GetRequiredService<IWhisperEngine>(),
    provider.GetRequiredService<IAudioPreparer>()
));

// Alignment is a no-op seam now that the transcriber returns words itself; it
// stays so that a forced aligner can be reintroduced without touching the
// pipeline (ADR-0006).
builder.Services.AddSingleton<IAligner, PassThroughAligner>();

// UNLOAD_MODELS_AFTER_JOB resolves to an implementation rather than a branch in
// the pipeline, which releases the transcriber after every job regardless. With
// the flag off the wrapper is the no-op that keeps the model resident.
builder.Services.AddSingleton<ITranscriber>(provider => new ReleasePolicyTranscriber(
    provider.GetRequiredService<WhisperTranscriber>(),
    provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value
));

// The transcript cache belongs to the job's own transcript directory.
builder.Services.AddSingleton<TranscriptStoreFactory>(_ =>
    directory => new TranscriptStore(directory)
);

builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<SmartCutOptions>>().Value
);

builder.Services.AddSingleton<IMediaPipeline, MediaPipeline>();

// Web video (docs/04-web-video-plan.md). yt-dlp and Deno are user-installed
// prerequisites like FFmpeg, found on PATH unless the Sources section names
// them. The source runs them through the same process runner FFmpeg uses.
builder.Services.Configure<SourcesOptions>(
    builder.Configuration.GetSection(SourcesOptions.SectionName)
);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddKeyedSingleton<IWebAudioSource>(
    DevFileProvider.RealSourceKey,
    (provider, _) =>
        new YtDlpAudioSource(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IOptions<SourcesOptions>>().Value
        )
);

// W16: the development-only `file` provider (Watch:DevFileProvider), off unless
// configured. On, Watch Sessions also accept provider "file" for the files in a
// directory, and /dev/harness/ serves the end-to-end harness page. Read here,
// at startup, because it decides what is composed and mapped; misconfigured,
// the host refuses to start.
var devFiles = DevFileProvider.Read(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    AppContext.BaseDirectory
);
builder.Services.AddSingleton(devFiles);
builder.Services.AddSingleton(new WatchVideos(allowFiles: devFiles.Enabled));
builder.Services.AddSingleton<IWebAudioSource>(provider =>
{
    var real = provider.GetRequiredKeyedService<IWebAudioSource>(DevFileProvider.RealSourceKey);
    return devFiles.Files is { } files ? new DevFileAudioSource(real, files) : real;
});

// /config reports whether that works here. Probing starts yt-dlp, which is
// slow to start, so the answer is kept and the first probe begins with the
// host rather than with the UI's first page load.
builder.Services.AddSingleton<WebVideoAvailabilityCache>();
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<WebVideoAvailabilityCache>()
);

// Watch Sessions share what Jobs use: the one IWhisperEngine singleton above
// (so both queue on its one priority lane, sessions ahead of jobs), the
// transcript cache through TranscriptStoreFactory, and the Bad Words List. The
// Watch section sets only the timings; the paths always follow Storage, so
// there is one place to move the data rather than two that can disagree. A
// session's scratch lives under scratch/, which StorageHousekeeping empties at
// startup, so a crashed session's download goes with the jobs' leftovers.
builder.Services.AddWatch(builder.Configuration);
builder
    .Services.AddOptions<WatchOptions>()
    .PostConfigure<IOptions<StorageOptions>>(
        (watch, storage) =>
        {
            watch.ScratchDirectory = Path.Combine(storage.Value.ScratchDirectory, "watch");
            watch.TranscriptDirectory = storage.Value.ResolvedTranscriptDirectory;
            watch.BadWordsPath = storage.Value.BadWordsPath;
        }
    );

// A crash trace, when a path is configured (Diagnostics:CrashTracePath, or the
// FFN_CRASH_TRACE variable). Off otherwise, and registered before the host is
// built so the heartbeat can see whether it is on. whisper.cpp can end this
// process without unwinding - exit code 0xC0000409 - and a buffered log loses
// the lines that would say where; this one is on the disk line by line.
var tracePath =
    builder.Configuration["Diagnostics:CrashTracePath"]
    ?? Environment.GetEnvironmentVariable("FFN_CRASH_TRACE");

if (!string.IsNullOrWhiteSpace(tracePath) && CrashTrace.Start(tracePath))
{
    builder.Services.AddHostedService<CrashTraceHeartbeat>();
}

var app = builder.Build();

if (CrashTrace.IsEnabled)
{
    app.Logger.LogWarning("Crash trace is being written to {Path}", CrashTrace.Path);

    // A clean shutdown says so, so a file that simply stops is known to be a
    // process that was killed rather than one that was asked to stop.
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        CrashTrace.Write("process.exit");
        CrashTrace.Stop();
    };

    AppDomain.CurrentDomain.UnhandledException += (_, unhandled) =>
        CrashTrace.Write("process.unhandled", unhandled.ExceptionObject.ToString() ?? "");
}

// ALIGN_DEVICE, WHISPER_MULTI_GPU and the other ROCm-era variables do nothing
// now. An old .env that sets them is told so rather than silently ignored.
LegacyEnvironmentVariables.WarnAboutRetiredVariables(app.Logger);
DevFileProvider.WarnIfEnabled(devFiles, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapUserInterface();
app.MapJobEndpoints();
app.MapConfigEndpoint();
app.MapEventEndpoint();
app.MapWatchEndpoints();
app.MapDevHarness(devFiles);

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory</c> can target this host in tests.</summary>
public partial class Program;
