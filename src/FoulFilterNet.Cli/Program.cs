using System.CommandLine;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Media;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Cli;

/// <summary>
/// The terminal entry point. The web service calls the pipeline over HTTP; this
/// wrapper exists for one-off runs, and composes the same engines
/// <c>FoulFilterNet.Web</c> does.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var commandLine = new FoulFilterCommandLine();

        commandLine.Command.SetAction((parseResult, cancellationToken) =>
            RunAsync(commandLine, parseResult, cancellationToken));

        // JobRunner reports every job failure as one line and an exit code. The
        // default handler stays on only for what happens before it runs - a
        // host that cannot be built is a broken install, and a stack trace is
        // the useful report for that.
        return await commandLine.Command.Parse(args).InvokeAsync(
            new InvocationConfiguration { EnableDefaultExceptionHandler = true });
    }

    private static async Task<int> RunAsync(
        FoulFilterCommandLine commandLine,
        ParseResult parsed,
        CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();

        // The Python's basicConfig(INFO) put engine chatter on the terminal
        // alongside its own output. Here progress is the terminal's job, so the
        // engines stay quiet unless --debug asks for them.
        builder.Logging.SetMinimumLevel(
            parsed.GetValue(commandLine.Debug) ? LogLevel.Information : LogLevel.Warning);

        builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection("Transcription"));
        builder.Services.AddSmartCut(builder.Configuration);

        builder.Services.AddSingleton<IFFmpegRunner, FFmpegRunner>();
        builder.Services.AddSingleton<IMediaProber, FFprobeMediaProber>();
        builder.Services.AddSingleton<IAudioPreparer, FFmpegAudioPreparer>();
        builder.Services.AddSingleton<IMediaEditor, MediaEditor>();

        // No acquisition delegate, deliberately: a terminal run fails with the
        // path it wanted rather than quietly pulling 1.6 GB over the network.
        // Web wires the downloader; the CLI does not.
        builder.Services.AddSingleton<IWhisperEngine>(provider => new WhisperNetEngine(
            provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value,
            new WhisperModelSource(
                provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value.ModelDirectory),
            provider.GetRequiredService<ILogger<WhisperNetEngine>>()));

        builder.Services.AddSingleton<WhisperTranscriber>(provider => new WhisperTranscriber(
            provider.GetRequiredService<IWhisperEngine>(),
            provider.GetRequiredService<IAudioPreparer>()));

        builder.Services.AddSingleton<IAligner, PassThroughAligner>();

        // UNLOAD_MODELS_AFTER_JOB is honoured by the wrapper, not by the
        // pipeline, so the bare transcriber is never registered on its own.
        builder.Services.AddSingleton<ITranscriber>(provider => new ReleasePolicyTranscriber(
            provider.GetRequiredService<WhisperTranscriber>(),
            provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value));

        builder.Services.AddSingleton<TranscriptStoreFactory>(_ => directory => new TranscriptStore(directory));
        builder.Services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<SmartCutOptions>>().Value);
        builder.Services.AddSingleton<IMediaPipeline, MediaPipeline>();

        using var host = builder.Build();

        // Configuration carries the legacy variable names, so CENSOR_METHOD and
        // TRANSCRIPT_DIR work from the environment and from appsettings alike.
        var request = commandLine.ToRequest(parsed, key => builder.Configuration[key]);

        var runner = new JobRunner(
            host.Services.GetRequiredService<IMediaProber>(),
            host.Services.GetRequiredService<IMediaPipeline>(),
            Console.Out,
            Console.Error,
            interactive: !Console.IsOutputRedirected);

        return await runner.RunAsync(request, cancellationToken);
    }
}
