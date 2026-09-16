using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Jobs;
using FoulFilterNet.Transcription;
using FoulFilterNet.Web;
using FoulFilterNet.Web.Endpoints;

// Composition root.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<SmartCutOptions>(
    builder.Configuration.GetSection(SmartCutOptions.SectionName));
builder.Services.Configure<TranscriptionOptions>(
    builder.Configuration.GetSection("Transcription"));

// The front end and the Python it was written against both speak snake_case.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddSingleton<JobEventFanOut>();
builder.Services.AddSingleton<JobManager>();

// Registered before the worker: hosted services start in registration order, so
// the directories exist and last run's orphans are gone before a job can run.
builder.Services.AddHostedService<StorageHousekeeping>();
builder.Services.AddHostedService<JobWorker>();

// T21 replaces this registration with the real orchestrator.
builder.Services.AddSingleton<IMediaPipeline, PendingMediaPipeline>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapUserInterface();
app.MapJobEndpoints();
app.MapConfigEndpoint();
app.MapEventEndpoint();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory</c> can target this host in tests.</summary>
public partial class Program;
