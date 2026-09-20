using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// Stands in for a whisper.cpp processor: hears whatever it is told to, and
/// remembers what it was given and whether the lane was held when it was.
/// </summary>
internal sealed class FakeListener(
    InferenceLane lane,
    Func<float[], CancellationToken, Task<TranscriptionResult>>? hear = null
) : IWindowListener
{
    public List<float[]> Heard { get; } = [];

    public List<bool> LaneHeldWhileHearing { get; } = [];

    public bool Disposed { get; private set; }

    public bool LaneHeldWhenDisposed { get; private set; }

    public Task<TranscriptionResult> HearAsync(float[] samples, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Heard.Add(samples);
        LaneHeldWhileHearing.Add(lane.IsHeld);
        return hear?.Invoke(samples, cancellationToken)
            ?? Task.FromResult(
                new TranscriptionResult(
                    [new Segment(1.0, 2.0, $"heard {samples.Length}")],
                    [new Word("heard", 1.0, 2.0)]
                )
            );
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        LaneHeldWhenDisposed = lane.IsHeld;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A minute of analysis audio on disk and the lane its windows share.</summary>
internal sealed class WindowedAudioRig : IDisposable
{
    private readonly TempDirectory _directory = new();

    public WindowedAudioRig()
    {
        Path = System.IO.Path.Combine(_directory.Path, "analysis.wav");
        TestWav.Write(Path, 60L * 16000, (frame, _) => (short)(frame % 30000));
    }

    public string Path { get; }

    public InferenceLane Lane { get; } = new();

    public List<FakeListener> Listeners { get; } = [];

    public int Closed { get; private set; }

    public Func<float[], CancellationToken, Task<TranscriptionResult>>? Hear { get; set; }

    public WindowedAudio Open(InferencePriority priority = InferencePriority.Normal) =>
        new(
            AnalysisWav.OpenAsync(Path, CancellationToken.None).Result,
            Lane,
            priority,
            () =>
            {
                var listener = new FakeListener(Lane, Hear);
                Listeners.Add(listener);
                return listener;
            },
            () =>
            {
                Closed++;
                return ValueTask.CompletedTask;
            }
        );

    public void Dispose() => _directory.Dispose();
}

/// <summary>
/// One window of opened audio: read from the file, heard on the GPU with the
/// lane held, and handed back on the window's own timeline, untouched.
/// </summary>
public sealed class WhenTranscribingOneWindowOfOpenedAudio : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _heard;

    public WhenTranscribingOneWindowOfOpenedAudio()
    {
        _sut = _rig.Open();
        _heard = _sut.TranscribeWindowAsync(1, TestContext.Current.CancellationToken).Result;

        _rig.Listeners.ShouldHaveSingleItem();
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void ReportsTheAudiosDuration() => _sut.DurationSeconds.ShouldBe(60.0);

    [Fact]
    public void ReportsTheAudiosWindows() => _sut.Windows.ShouldBe(TranscriptionWindows.Plan(60.0));

    [Fact]
    public void RemembersItsPriority() => _sut.Priority.ShouldBe(InferencePriority.Normal);

    [Fact]
    public void HandsTheListenerTheWholeWindowAndSomeSilence() =>
        _rig.Listeners[0]
            .Heard.ShouldHaveSingleItem()
            .Length.ShouldBe((int)((28 + WindowedAudio.TrailingSilenceSeconds) * 16000));

    /// <summary>
    /// The silence is what keeps whisper.cpp from ending the window's last
    /// segment on a sliver too short for DTW to filter, which is a fast-fail
    /// rather than an exception.
    /// </summary>
    [Fact]
    public void EndsThatWindowInSilence() => _rig.Listeners[0].Heard[0][^1].ShouldBe(0f);

    [Fact]
    public void KeepsTheWindowsOwnAudioInFront() =>
        _rig.Listeners[0].Heard[0][28 * 16000 - 1].ShouldNotBe(0f);

    [Fact]
    public void HandsTheListenerThatWindowsAudio() =>
        _rig.Listeners[0].Heard[0][0].ShouldBe(22000 / 32768f);

    [Fact]
    public void HearsItWithTheLaneHeld() => _rig.Listeners[0].LaneHeldWhileHearing.ShouldBe([true]);

    [Fact]
    public void LeavesTheLaneFreeAfterwards() => _rig.Lane.IsHeld.ShouldBeFalse();

    [Fact]
    public void ReportsWhatTheListenerHeardOnTheWindowsOwnTimeline() =>
        _heard.Words.ShouldHaveSingleItem().Start.ShouldBe(1.0);

    [Fact]
    public void KeepsTheListenerForTheNextWindow() => _rig.Listeners[0].Disposed.ShouldBeFalse();
}

/// <summary>
/// Whatever whisper.cpp says about the silence after a window is not part of
/// the file, so it is dropped rather than stitched into the transcript - which
/// matters most for the last window, whose share has no end.
/// </summary>
public sealed class WhenTheListenerHearsIntoTheTrailingSilence : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _heard;

    public WhenTheListenerHearsIntoTheTrailingSilence()
    {
        _rig.Hear = (_, _) =>
            Task.FromResult(
                new TranscriptionResult(
                    [
                        new Segment(1.0, 2.0, "inside"),
                        new Segment(27.9, 28.4, "across the end"),
                        new Segment(28.1, 28.4, "in the silence"),
                    ],
                    [
                        new Word("inside", 1.0, 2.0),
                        new Word("across", 27.9, 28.4),
                        new Word("silence", 28.1, 28.4),
                    ]
                )
            );

        _sut = _rig.Open();

        _heard = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void KeepsWhatBeganInsideTheWindow() =>
        _heard.Segments.Select(segment => segment.Text).ShouldBe(["inside", "across the end"]);

    [Fact]
    public void KeepsTheWordsThatBeganInsideTheWindow() =>
        _heard.Words.Select(word => word.Text).ShouldBe(["inside", "across"]);
}

/// <summary>
/// A processor is built once per opened file and reused for every window, as the
/// batch path always did - and not at all until a window is asked for, so
/// audio opened but never transcribed holds no GPU state.
/// </summary>
public sealed class WhenTranscribingEveryWindowOfOpenedAudio : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;

    public WhenTranscribingEveryWindowOfOpenedAudio()
    {
        _sut = _rig.Open();

        _rig.Listeners.ShouldBeEmpty();

        foreach (var index in new[] { 2, 0, 1, 0 })
        {
            _ = _sut.TranscribeWindowAsync(index, TestContext.Current.CancellationToken).Result;
        }
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void BuildsOneListenerForAllOfThem() => _rig.Listeners.ShouldHaveSingleItem();

    [Fact]
    public void HearsEveryRequestInTheOrderAsked() =>
        _rig.Listeners[0].Heard.Select(s => s[0]).ShouldBe([2000 / 32768f, 0f, 22000 / 32768f, 0f]);
}

public sealed class WhenOpenedAudioIsClosedWithoutTranscribing : IDisposable
{
    private readonly WindowedAudioRig _rig = new();

    public WhenOpenedAudioIsClosedWithoutTranscribing() =>
        _rig.Open().DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void NeverBuiltAListener() => _rig.Listeners.ShouldBeEmpty();

    [Fact]
    public void StillTellsTheEngineItClosed() => _rig.Closed.ShouldBe(1);
}

/// <summary>
/// Closing hands the processor back and tells the engine, once, however often
/// it is asked - the engine counts open audio to know when it may drop the
/// model.
/// </summary>
public sealed class WhenOpenedAudioIsClosed : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;

    public WhenOpenedAudioIsClosed()
    {
        _sut = _rig.Open();
        _ = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;

        _sut.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
        _sut.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void DisposesTheListener() => _rig.Listeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void TellsTheEngineExactlyOnce() => _rig.Closed.ShouldBe(1);

    [Fact]
    public async Task RefusesToTranscribeAnotherWindow() =>
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
        );

    [Fact]
    public void LeavesTheLaneFree() => _rig.Lane.IsHeld.ShouldBeFalse();
}

/// <summary>
/// A Watch Session's audio opened at High goes ahead of a Job's window already
/// waiting at Normal: the priority travels with the opened audio.
/// </summary>
public sealed class WhenWatchAudioAndJobAudioWaitForTheSameLane : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly List<string> _order = [];

    public WhenWatchAudioAndJobAudioWaitForTheSameLane()
    {
        var job = _rig.Open(InferencePriority.Normal);
        var watch = _rig.Open(InferencePriority.High);
        _rig.Hear = (samples, _) =>
        {
            _order.Add(samples[0] == 0f ? "job" : "watch");
            return Task.FromResult(new TranscriptionResult([], []));
        };

        var holder = _rig.Lane.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;
        var jobWindow = Task.Run(() =>
            job.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
        );
        SpinWait.SpinUntil(() => _rig.Lane.Waiting == 1, TimeSpan.FromSeconds(10));
        var watchWindow = Task.Run(() =>
            watch.TranscribeWindowAsync(1, TestContext.Current.CancellationToken)
        );
        SpinWait.SpinUntil(() => _rig.Lane.Waiting == 2, TimeSpan.FromSeconds(10));

        holder.Dispose();
        Task.WhenAll(jobWindow, watchWindow).Wait(TestContext.Current.CancellationToken);
        job.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
        watch.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void HearsTheWatchWindowFirst() => _order.ShouldBe(["watch", "job"]);
}

/// <summary>
/// A window waiting its turn behind another is cancelled: it never touches the
/// GPU, and the lane is not left holding a place for it.
/// </summary>
public sealed class WhenAWindowIsCancelledWhileItWaitsForTheLane : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly Exception? _thrown;

    public WhenAWindowIsCancelledWhileItWaitsForTheLane()
    {
        _sut = _rig.Open();
        var holder = _rig.Lane.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;
        using var abandon = new CancellationTokenSource();

        var window = Task.Run(() => _sut.TranscribeWindowAsync(0, abandon.Token));
        SpinWait.SpinUntil(() => _rig.Lane.Waiting == 1, TimeSpan.FromSeconds(10));
        abandon.Cancel();
        _thrown = Record.Exception(() => window.GetAwaiter().GetResult());

        holder.Dispose();
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void ThrowsOperationCanceled() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void NeverReachedTheGpu() => _rig.Listeners.ShouldBeEmpty();

    [Fact]
    public void LeftNobodyWaiting() => _rig.Lane.Waiting.ShouldBe(0);

    [Fact]
    public async Task CanStillTranscribeTheWindowAfterwards() =>
        (
            await _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
        ).Words.ShouldHaveSingleItem();
}

/// <summary>
/// A window cancelled while whisper.cpp is working on it. Whisper.net stops the
/// native call through an abort callback, which can land after the managed
/// enumeration has already thrown - so the processor is disposed (its
/// <c>DisposeAsync</c> waits for the native call) <em>before</em> the lane is
/// handed on, and the next window gets a fresh one. That is what keeps
/// inference strictly one at a time even across a cancellation.
/// </summary>
public sealed class WhenAWindowIsCancelledWhileItIsHeard : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly Exception? _thrown;

    public WhenAWindowIsCancelledWhileItIsHeard()
    {
        _sut = _rig.Open();
        var hearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Hear = async (_, cancellationToken) =>
        {
            hearing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new TranscriptionResult([], []);
        };
        using var abandon = new CancellationTokenSource();

        var window = Task.Run(() => _sut.TranscribeWindowAsync(0, abandon.Token));
        hearing.Task.Wait(TestContext.Current.CancellationToken);
        abandon.Cancel();
        _thrown = Record.Exception(() => window.GetAwaiter().GetResult());

        _rig.Hear = null;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void ThrowsOperationCanceled() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void DisposesTheInterruptedListener() => _rig.Listeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void DisposesItBeforeLettingGoOfTheLane() =>
        _rig.Listeners[0].LaneHeldWhenDisposed.ShouldBeTrue();

    [Fact]
    public void LeavesTheLaneFree() => _rig.Lane.IsHeld.ShouldBeFalse();

    [Fact]
    public async Task HearsTheNextWindowWithAFreshListener()
    {
        await _sut.TranscribeWindowAsync(1, TestContext.Current.CancellationToken);

        _rig.Listeners.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HearsTheNextWindowProperly() =>
        (
            await _sut.TranscribeWindowAsync(1, TestContext.Current.CancellationToken)
        ).Words.ShouldHaveSingleItem();
}

/// <summary>
/// A failed inference fails the window, not the GPU: the lane is free for the
/// next request.
/// </summary>
public sealed class WhenHearingAWindowFails : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly Exception? _thrown;

    public WhenHearingAWindowFails()
    {
        _sut = _rig.Open();
        _rig.Hear = (_, _) => throw new InvalidOperationException("cuda oom");

        _thrown = Record.Exception(() =>
            _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult()
        );
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void ReportsTheFailure() => _thrown.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void LeavesTheLaneFree() => _rig.Lane.IsHeld.ShouldBeFalse();
}

/// <summary>
/// Closing while a window is on the GPU waits for that window rather than
/// pulling the processor out from under whisper.cpp.
/// </summary>
public sealed class WhenOpenedAudioIsClosedMidWindow : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly TaskCompletionSource<TranscriptionResult> _finish = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Task<TranscriptionResult> _window;
    private readonly Task _closing;
    private readonly bool _disposedBeforeTheWindowFinished;
    private readonly bool _closedBeforeTheWindowFinished;

    public WhenOpenedAudioIsClosedMidWindow()
    {
        var sut = _rig.Open();
        var hearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Hear = (_, _) =>
        {
            hearing.TrySetResult();
            return _finish.Task;
        };

        _window = Task.Run(() =>
            sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
        );
        hearing.Task.Wait(TestContext.Current.CancellationToken);
        _closing = Task.Run(() => sut.DisposeAsync().AsTask());

        Thread.Sleep(50);
        _disposedBeforeTheWindowFinished = _rig.Listeners[0].Disposed;
        _closedBeforeTheWindowFinished = _closing.IsCompleted;

        _finish.SetResult(new TranscriptionResult([], []));
        Task.WhenAll(_window, _closing).Wait(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void DoesNotDisposeTheListenerWhileItIsHearing() =>
        _disposedBeforeTheWindowFinished.ShouldBeFalse();

    [Fact]
    public void WaitsForTheWindowToFinish() => _closedBeforeTheWindowFinished.ShouldBeFalse();

    [Fact]
    public void StillDeliversTheWindow() => _window.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void DisposesTheListenerOnceItIsDone() => _rig.Listeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void TellsTheEngineItClosed() => _rig.Closed.ShouldBe(1);
}

public sealed class WhenAWindowOutsideThePlanIsAskedFor : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;

    public WhenAWindowOutsideThePlanIsAskedFor() => _sut = _rig.Open();

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task RefusesIt(int index) =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.TranscribeWindowAsync(index, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task NeitherTakesTheLaneNorBuildsAListener()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.TranscribeWindowAsync(3, TestContext.Current.CancellationToken)
        );

        (_rig.Lane.IsHeld || _rig.Listeners.Count > 0).ShouldBeFalse();
    }
}

public sealed class WhenWindowedAudioIsBuiltWithoutCollaborators : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _wav;

    public WhenWindowedAudioIsBuiltWithoutCollaborators()
    {
        var path = Path.Combine(_directory.Path, "a.wav");
        TestWav.Write(path, 16000, (_, _) => 0);
        _wav = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _wav.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void RefusesMissingAudio() =>
        Should.Throw<ArgumentNullException>(() =>
            new WindowedAudio(null!, new InferenceLane(), InferencePriority.Normal, Listen)
        );

    [Fact]
    public void RefusesAMissingLane() =>
        Should.Throw<ArgumentNullException>(() =>
            new WindowedAudio(_wav, null!, InferencePriority.Normal, Listen)
        );

    [Fact]
    public void RefusesNoWayToListen() =>
        Should.Throw<ArgumentNullException>(() =>
            new WindowedAudio(_wav, new InferenceLane(), InferencePriority.Normal, null!)
        );

    [Fact]
    public void RefusesAnUnknownPriority() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new WindowedAudio(_wav, new InferenceLane(), (InferencePriority)7, Listen)
        );

    private static FakeListener Listen() => new FakeListener(new InferenceLane());
}
