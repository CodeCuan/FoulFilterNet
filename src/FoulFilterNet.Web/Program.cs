using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Jobs;
using FoulFilterNet.Media;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using FoulFilterNet.Transcription;
using FoulFilterNet.Web;
using FoulFilterNet.Web.Endpoints;
using Microsoft.Extensions.Options;

// Composition root.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<TranscriptionOptions>(
    builder.Configuration.GetSection("Transcription"));

// Binds the SmartCut section, registers the named client, and resolves exactly
// one ISmartCutAdvisor - the no-op when the feature is off or unconfigured. The
// pipeline therefore never asks whether the feature is on.
builder.Services.AddSmartCut(builder.Configuration);

// The front end and the Python it was written against both speak snake_case.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

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

// T13 is the one engine still missing; until it lands a job that actually needs
// transcribing fails with an explanation, and a cached transcript still runs.
builder.Services.AddSingleton<ITranscriber, PendingTranscriber>();
builder.Services.AddSingleton<IAligner, PassThroughAligner>();

// The transcript cache belongs to the job's own transcript directory.
builder.Services.AddSingleton<TranscriptStoreFactory>(
    _ => directory => new TranscriptStore(directory));

builder.Services.AddSingleton(
    provider => provider.GetRequiredService<IOptions<SmartCutOptions>>().Value);

builder.Services.AddSingleton<IMediaPipeline, MediaPipeline>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapUserInterface();
app.MapJobEndpoints();
app.MapConfigEndpoint();
app.MapEventEndpoint();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory</c> can target this host in tests.</summary>
public partial class Program;
