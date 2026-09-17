using System.Globalization;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Cli;

/// <summary>
/// One terminal run: recognise the file, drive the pipeline, report what it
/// found. The web service reports the same checkpoints over SSE; this prints
/// them.
/// </summary>
public sealed class JobRunner
{
    /// <summary>A cancelled run, by the shell's convention for an interrupt.</summary>
    public const int CancelledExitCode = 130;

    private readonly IMediaProber _prober;
    private readonly IMediaPipeline _pipeline;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _interactive;

    public JobRunner(
        IMediaProber prober,
        IMediaPipeline pipeline,
        TextWriter output,
        TextWriter error,
        bool interactive = false
    )
    {
        ArgumentNullException.ThrowIfNull(prober);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _prober = prober;
        _pipeline = pipeline;
        _output = output;
        _error = error;
        _interactive = interactive;
    }

    public async Task<int> RunAsync(
        JobRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var progress = new ConsoleProgressReporter(_output, _interactive);

        try
        {
            // FFprobe answers what libmagic used to, and an unreadable file is one
            // printed line rather than a failure deep inside the pipeline. A path
            // FFprobe cannot open at all is reported the same way, below.
            var media = await _prober.ProbeAsync(request.InputPath, cancellationToken);
            if (media.Kind == MediaKind.Unknown)
            {
                await _error.WriteLineAsync($"Error: Unrecognized file type: {request.InputPath}");
                return 1;
            }

            var summary = await _pipeline.RunAsync(request, progress, cancellationToken);
            progress.Finish();
            await ReportAsync(summary, request);
            return 0;
        }
        catch (JobCancelledException)
        {
            progress.Finish();
            await _error.WriteLineAsync("Cancelled.");
            return CancelledExitCode;
        }
        catch (OperationCanceledException)
        {
            progress.Finish();
            await _error.WriteLineAsync("Cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            // A terminal wants the reason on one line, not a stack trace - the
            // same call the worker makes when a job fails behind the web UI.
            progress.Finish();
            await _error.WriteLineAsync($"Error: {exception.Message}");
            return 1;
        }
    }

    private async Task ReportAsync(JobSummary summary, JobRequest request)
    {
        if (summary.Hits.Count > 0)
        {
            await _output.WriteLineAsync($"{summary.Hits.Count} hit(s):");
            foreach (var hit in summary.Hits)
            {
                await _output.WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {hit.Start, 8:F2} - {hit.End, 8:F2}  {hit.Phrase}"
                    )
                );
            }
        }
        else
        {
            await _output.WriteLineAsync("No inappropriate words found.");
        }

        await _output.WriteLineAsync(
            $"Done. Output: {(request.Render ? request.OutputPath : "(not written)")}"
        );
    }
}

/// <summary>
/// The pipeline's stage checkpoints as a terminal can show them: one rewritten
/// line where the output is a console, one line per checkpoint where it is a
/// pipe or a log file.
/// </summary>
public sealed class ConsoleProgressReporter : IProgress<JobProgress>
{
    private readonly TextWriter _writer;
    private readonly bool _interactive;
    private int _width;
    private bool _wrote;

    public ConsoleProgressReporter(TextWriter writer, bool interactive)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
        _interactive = interactive;
    }

    public void Report(JobProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"  [{value.Percent, 3}%] {value.Stage}{(value.Detail.Length > 0 ? " " + value.Detail : string.Empty)}"
        );

        _wrote = true;

        if (!_interactive)
        {
            _writer.WriteLine(line);
            return;
        }

        // Pad to the longest line written so far so a shorter stage name cannot
        // leave the tail of a longer one on screen.
        _writer.Write('\r');
        _writer.Write(line.PadRight(_width));
        _width = Math.Max(_width, line.Length);
        _writer.Flush();
    }

    /// <summary>Close off the progress line so the report starts on its own.</summary>
    public void Finish()
    {
        if (_interactive && _wrote)
        {
            _writer.WriteLine();
        }
    }
}
