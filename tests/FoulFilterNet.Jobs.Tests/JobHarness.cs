using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.Jobs.Tests;

/// <summary>
/// A real <see cref="JobManager"/> and a real <see cref="JobWorker"/> over a stub
/// pipeline and a throwaway data directory, so tests exercise the queue's actual
/// threading rather than a simulation of it.
/// </summary>
internal sealed class JobHarness : IAsyncDisposable
{
    private readonly JobWorker _worker;
    private readonly string _root;

    public JobHarness()
    {
        _root = Path.Combine(Path.GetTempPath(), "ffn-jobs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "uploads"));
        Directory.CreateDirectory(Path.Combine(_root, "outputs"));
        Directory.CreateDirectory(Path.Combine(_root, "scratch"));

        Jobs = new JobManager();
        Pipeline = new StubMediaPipeline();
        _worker = new JobWorker(Jobs, Pipeline, NullLogger<JobWorker>.Instance);
    }

    public JobManager Jobs { get; }

    public StubMediaPipeline Pipeline { get; }

    public Task StartAsync() => _worker.StartAsync(CancellationToken.None);

    /// <summary>Enqueues a job whose input file exists on disk, as an upload would.</summary>
    public JobRecord Enqueue(string fileName)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var request = new JobRequest
        {
            InputPath = Path.Combine(_root, "uploads", $"{id}__{fileName}"),
            OutputPath = Path.Combine(_root, "outputs", $"censored_{fileName}"),
            BadWordsPath = Path.Combine(_root, "bad_words.txt"),
            TranscriptDirectory = Path.Combine(_root, "transcripts"),
            ScratchDirectory = Path.Combine(_root, "scratch", id),
        };

        File.WriteAllText(request.InputPath, "input");
        Directory.CreateDirectory(request.ScratchDirectory);
        File.WriteAllText(Path.Combine(request.ScratchDirectory, "work.wav"), "scratch");

        return Jobs.Enqueue(id, fileName, request);
    }

    public JobRecord Get(string id) =>
        Jobs.Find(id) ?? throw new InvalidOperationException($"No job {id}.");

    /// <summary>Polls until <paramref name="predicate"/> holds, or fails the test.</summary>
    public async Task<JobRecord> WaitForAsync(string id, Func<JobRecord, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var record = Get(id);
            if (predicate(record))
            {
                return record;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Job {id} never reached the expected state (was {Get(id).Status}).");
    }

    public Task<JobRecord> WaitForStatusAsync(string id, JobStatus status) =>
        WaitForAsync(id, record => record.Status == status);

    public async ValueTask DisposeAsync()
    {
        Jobs.CompleteAdding();
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A throwaway directory that outlives the test run is harmless.
        }
    }
}
