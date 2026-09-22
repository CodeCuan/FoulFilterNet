using System.Collections.Concurrent;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// A point a fake stops at until the test lets it through: the test waits for
/// <see cref="Entered"/>, looks at the session, then calls <see cref="Release"/>.
/// Cancelling the fake's token while it waits throws, as real work would.
/// </summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public void WaitUntilEntered() => Waits.On(Entered);

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>Bounded waits, so a broken session fails a test instead of hanging it.</summary>
internal static class Waits
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static void On(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();

    /// <summary>Poll until <paramref name="condition"/> holds; true, or a timeout.</summary>
    public static bool Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never became true.");
            }

            Thread.Sleep(5);
        }

        return true;
    }
}

internal sealed class FakeAudioSource : IWebAudioSource
{
    private int _calls;

    public string Title { get; set; } = "Me at the zoo";

    public double? DurationSeconds { get; set; } = 101.0;

    public WebAudioException? Failure { get; set; }

    public Gate? Hold { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<string> Directories { get; } = new();

    public ConcurrentQueue<string> AudioPaths { get; } = new();

    public async Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _calls);
        Directories.Enqueue(directory);

        // Written before any failure: the session, not the source, is what
        // this suite holds responsible for the scratch directory.
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "audio.webm");
        await File.WriteAllTextAsync(path, "not really audio", CancellationToken.None);
        AudioPaths.Enqueue(path);

        if (Hold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return new WebAudio(video, Title, DurationSeconds, path, "webm", "251", "opus", false);
    }

    public Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    ) => Task.FromResult(new WebVideoAvailability("2026.09.01", "2.5.0"));

    public static WebAudioException Failing(WebAudioFailure kind, string reason) =>
        new(Harness.Video, kind, reason, $"yt-dlp failed: {reason}", exitCode: 1);
}

internal sealed class FakePreparer(string directory) : IAudioPreparer, IAudioHeadPreparer
{
    /// <summary>What the head WAVs this preparer writes are called, so the fake engine knows one.</summary>
    public const string HeadPrefix = "ffn_head_";

    private int _calls;
    private int _headCalls;

    public int HeadCalls => Volatile.Read(ref _headCalls);

    public Exception? HeadFailure { get; set; }

    public Gate? HeadHold { get; set; }

    public ConcurrentQueue<(string Input, double Seconds)> HeadRequests { get; } = new();

    public ConcurrentQueue<string> Heads { get; } = new();

    /// <summary>The token the whole conversion was given.</summary>
    public CancellationToken WholeToken { get; private set; }

    public async Task<string> ConvertHeadAsync(
        string audioPath,
        double seconds,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _headCalls);
        HeadRequests.Enqueue((audioPath, seconds));

        if (HeadHold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        if (HeadFailure is { } failure)
        {
            throw failure;
        }

        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, $"{HeadPrefix}{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(wav, "not really the head of a wav", CancellationToken.None);
        Heads.Enqueue(wav);
        return wav;
    }

    public Exception? Failure { get; set; }

    public Gate? Hold { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<(string Input, double Offset)> Requests { get; } = new();

    public ConcurrentQueue<string> Prepared { get; } = new();

    public async Task<string> PadStartAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _calls);
        Requests.Enqueue((audioPath, offsetSeconds));
        WholeToken = cancellationToken;

        if (Hold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, $"ffn_pad_{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(wav, "not really a wav", CancellationToken.None);
        Prepared.Enqueue(wav);
        return wav;
    }

    public Task ExtractAudioTrackAsync(
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("A Watch Session never extracts a video's track.");

    public Task<string> CropAsync(
        string audioPath,
        double offsetSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("A Watch Session never crops.");
}

/// <summary>A preparer that cannot convert a head: the session converts the whole file first.</summary>
internal sealed class WholeFileOnlyPreparer(FakePreparer inner) : IAudioPreparer
{
    public Task<string> PadStartAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    ) => inner.PadStartAsync(audioPath, offsetSeconds, cancellationToken);

    public Task ExtractAudioTrackAsync(
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken = default
    ) => inner.ExtractAudioTrackAsync(videoPath, outputPath, cancellationToken);

    public Task<string> CropAsync(
        string audioPath,
        double offsetSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default
    ) => inner.CropAsync(audioPath, offsetSeconds, durationSeconds, cancellationToken);
}

/// <summary>
/// An engine whose audio is a <see cref="Script"/>: each window returns what
/// the script says it hears, after any <see cref="Gate"/> set for it, moving
/// the fake clock on by <see cref="WindowTime"/>.
/// </summary>
internal sealed class FakeEngine(Script script, FakeTimeProvider time) : IWhisperEngine
{
    private readonly ConcurrentDictionary<int, Gate> _holds = new();
    private int _warmUps;
    private int _warm;

    public Script Script => script;

    public Exception? OpenFailure { get; set; }

    public int? FailOnWindow { get; set; }

    public Func<int, TimeSpan> WindowTime { get; set; } = _ => TimeSpan.FromSeconds(1);

    public ConcurrentQueue<(string Wav, InferencePriority Priority)> Opened { get; } = new();

    public ConcurrentQueue<FakeAnalysisAudio> Audios { get; } = new();

    public ConcurrentQueue<int> Requested { get; } = new();

    /// <summary>Every window asked for, with whether it was the head's or the whole file's.</summary>
    public ConcurrentQueue<(bool FromHead, int Index)> RequestedFrom { get; } = new();

    /// <summary>What a head WAV (<see cref="FakePreparer.HeadPrefix"/>) holds; null refuses to open one.</summary>
    public Script? HeadScript { get; set; }

    public IEnumerable<int> HeardFromHead =>
        RequestedFrom.Where(r => r.FromHead).Select(r => r.Index);

    public IEnumerable<int> HeardFromWhole =>
        RequestedFrom.Where(r => !r.FromHead).Select(r => r.Index);

    public ConcurrentQueue<int> Abandoned { get; } = new();

    public Gate Hold(int window) => _holds.GetOrAdd(window, _ => new Gate());

    public Task<IAnalysisAudio> OpenAsync(
        string wavPath,
        InferencePriority priority = InferencePriority.Normal,
        CancellationToken cancellationToken = default
    )
    {
        WarmWhenOpened.Enqueue(IsWarm);
        Opened.Enqueue((wavPath, priority));
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        var fromHead = Path.GetFileName(wavPath)
            .StartsWith(FakePreparer.HeadPrefix, StringComparison.Ordinal);
        var heard = fromHead
            ? HeadScript ?? throw new InvalidOperationException("No head script was set.")
            : script;
        var audio = new FakeAnalysisAudio(this, time, heard, fromHead);
        Audios.Enqueue(audio);
        return Task.FromResult<IAnalysisAudio>(audio);
    }

    /// <summary>How many warm-ups were asked for.</summary>
    public int WarmUps => Volatile.Read(ref _warmUps);

    /// <summary>What each warm-up was told to stop on.</summary>
    public ConcurrentQueue<CancellationToken> WarmUpTokens { get; } = new();

    /// <summary>When set, a warm-up waits here before finishing.</summary>
    public Gate? WarmUpHold { get; set; }

    public Exception? WarmUpFailure { get; set; }

    /// <summary>Whether a warm-up had finished when each audio was opened.</summary>
    public ConcurrentQueue<bool> WarmWhenOpened { get; } = new();

    public bool IsWarm => Volatile.Read(ref _warm) == 1;

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _warmUps);
        WarmUpTokens.Enqueue(cancellationToken);

        if (WarmUpHold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        if (WarmUpFailure is { } failure)
        {
            throw failure;
        }

        Interlocked.Exchange(ref _warm, 1);
    }

    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    internal bool TryGetHold(int window, out Gate gate) => _holds.TryGetValue(window, out gate!);
}

internal sealed class FakeAnalysisAudio(
    FakeEngine engine,
    FakeTimeProvider time,
    Script script,
    bool fromHead
) : IAnalysisAudio
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public bool FromHead => fromHead;

    public double DurationSeconds => script.Duration;

    public IReadOnlyList<TranscriptionWindow> Windows => script.Plan;

    public async Task<TranscriptionResult> TranscribeWindowAsync(
        int index,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, script.Plan.Count);
        engine.Requested.Enqueue(index);
        engine.RequestedFrom.Enqueue((fromHead, index));

        try
        {
            if (engine.TryGetHold(index, out var gate))
            {
                await gate.PassAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            engine.Abandoned.Enqueue(index);
            throw;
        }

        if (engine.FailOnWindow == index)
        {
            throw new InvalidOperationException("The GPU fell over.");
        }

        time.Advance(engine.WindowTime(index));
        return script.Heard(index);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An in-memory Transcript cache: finds what was saved (or <see cref="Cached"/>),
/// matching digests ignoring case as <c>TranscriptStore</c> does.
/// </summary>
internal sealed class RecordingStore : ITranscriptStore
{
    private int _finds;

    public Transcript? Cached { get; set; }

    public Exception? SaveFailure { get; set; }

    public int Finds => Volatile.Read(ref _finds);

    public ConcurrentQueue<(Transcript Transcript, string BaseName)> Saves { get; } = new();

    public Task<string> ComputeHashAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("A Web Video is keyed by its VideoRef, not hashed.");

    /// <summary>When set, a lookup waits here first.</summary>
    public Gate? FindHold { get; set; }

    public async Task<Transcript?> FindAsync(
        string digest,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _finds);
        if (FindHold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        var saved = Saves
            .Select(s => s.Transcript)
            .LastOrDefault(t => string.Equals(t.FileHash, digest, StringComparison.Ordinal));
        var cached =
            Cached is { } c && string.Equals(c.FileHash, digest, StringComparison.OrdinalIgnoreCase)
                ? c
                : null;
        return saved ?? cached;
    }

    public Task SaveAsync(
        Transcript transcript,
        string baseName,
        CancellationToken cancellationToken = default
    )
    {
        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        Saves.Enqueue((transcript, baseName));
        return Task.CompletedTask;
    }
}

internal sealed class FakeBadWords(BadWordsList list) : IBadWordsSource
{
    private int _calls;

    public BadWordsList List { get; set; } = list;

    public Exception? Failure { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public BadWordsList GetCurrent()
    {
        Interlocked.Increment(ref _calls);
        return Failure is { } failure ? throw failure : List;
    }
}

/// <summary>
/// Everything a Watch Session or its manager is built from, faked, over the
/// hundred-second video of <see cref="HundredSecondVideo"/>, with a temporary
/// directory for scratch, prepared WAVs and transcripts.
/// </summary>
internal sealed class Harness : IDisposable
{
    public static readonly VideoRef Video = VideoRef.Create("youtube", "dQw4w9WgXcQ");

    public static readonly VideoRef OtherVideo = VideoRef.Create("youtube", "jNQXAC9IVRw");

    public static readonly DateTimeOffset Epoch = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public Harness()
        : this(HundredSecondVideo.Script) { }

    /// <param name="script">The video every whole WAV holds.</param>
    public Harness(Script script)
    {
        VideoScript = script;
        Root = Path.Combine(Path.GetTempPath(), "ffn-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Time = new FakeTimeProvider(Epoch);
        Source = new FakeAudioSource();
        Preparer = new FakePreparer(Path.Combine(Root, "prepared"));
        Engine = new FakeEngine(script, Time);
        Store = new RecordingStore();
        BadWords = new FakeBadWords(HundredSecondVideo.BadWords);
    }

    public string Root { get; }

    /// <summary>The video this harness's whole WAVs hold.</summary>
    public Script VideoScript { get; }

    /// <summary>What <see cref="WatchSessionServices.HeadSeconds"/> the sessions get.</summary>
    public double HeadSeconds { get; set; } = WatchOptions.DefaultHeadSeconds;

    /// <summary>How sessions pad their Hits; null leaves Watch's default.</summary>
    public CutPadding? CutPadding { get; set; }

    /// <summary>What the sessions convert with; the fake preparer unless replaced.</summary>
    public IAudioPreparer? AudioPreparer { get; set; }

    public string ScratchRoot => Path.Combine(Root, "scratch");

    public FakeTimeProvider Time { get; }

    public FakeAudioSource Source { get; }

    public FakePreparer Preparer { get; }

    public FakeEngine Engine { get; }

    public RecordingStore Store { get; }

    public FakeBadWords BadWords { get; }

    public static Script Script => HundredSecondVideo.Script;

    public WatchOptions Options() =>
        new()
        {
            ScratchDirectory = ScratchRoot,
            TranscriptDirectory = Path.Combine(Root, "transcripts"),
            BadWordsPath = Path.Combine(Root, "bad_words.txt"),
        };

    public WatchSessionServices Services(ITranscriptStore? store = null) =>
        new(
            Source,
            AudioPreparer ?? Preparer,
            Engine,
            store ?? Store,
            BadWords,
            ScratchRoot,
            Time,
            NullLogger.Instance,
            HeadSeconds,
            CutPadding
        );

    public WatchSession Session(ITranscriptStore? store = null) => new(Video, Services(store));

    public WatchSessionManager Manager(WatchOptions? options = null) =>
        new(
            Source,
            Preparer,
            Engine,
            Store,
            BadWords,
            options ?? Options(),
            Time,
            cutPadding: CutPadding
        );

    /// <summary>Start <paramref name="session"/> and wait for it to end.</summary>
    public static WatchSession Run(WatchSession session)
    {
        session.Start();
        Waits.On(session.Completion);
        return session;
    }

    /// <summary>The Transcript the whole video stitches to, as a Job would cache it.</summary>
    public static Transcript CachedTranscript(string key) =>
        new(Transcript.CurrentVersion, key, Script.Batch().Segments, Script.Batch().Words);

    public bool ScratchIsEmpty =>
        !Directory.Exists(ScratchRoot) || !Directory.EnumerateFileSystemEntries(ScratchRoot).Any();

    public bool EveryPreparedWavIsDeleted =>
        Preparer.Prepared.Concat(Preparer.Heads).All(p => !File.Exists(p));

    public bool EveryAudioIsDisposed => Engine.Audios.All(a => a.IsDisposed);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
