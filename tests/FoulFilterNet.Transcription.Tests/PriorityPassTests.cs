using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>Scripts for the prompted listener, keyed by which sub-window it is hearing.</summary>
internal static class PriorityScript
{
    /// <summary>
    /// A hearing that says <paramref name="said"/> for the sub-window heard
    /// <paramref name="call"/>-th (from zero), and nothing for the others.
    /// </summary>
    public static Func<float[], CancellationToken, Task<TranscriptionResult>> On(
        int call,
        params Word[] said
    )
    {
        var calls = 0;
        return (_, _) =>
            Task.FromResult(
                calls++ == call
                    ? new TranscriptionResult([new Segment(0.0, 5.0, "prompted")], said)
                    : new TranscriptionResult([], [])
            );
    }
}

/// <summary>
/// One window with the Priority Word Pass on: the window is heard as before,
/// then again in 5 s sub-windows by a prompted listener, and a priority word
/// only the sub-windows heard is added to the window's words - on the window's
/// timeline, in time order, with the segments left alone.
/// </summary>
public sealed class WhenAWindowIsHeardWithThePriorityWordPass : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _heard;

    /// <summary>
    /// Window 1 of the rig's minute is 22 s to 50 s and keeps 25 s to 41 s,
    /// which is 3 s to 19 s on its own timeline: the last three sub-windows
    /// can add nothing it keeps.
    /// </summary>
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = PriorityWindows.Plan(
        28.0,
        keepFrom: 3.0,
        keepTo: 19.0
    );

    public WhenAWindowIsHeardWithThePriorityWordPass()
    {
        _rig.Hear = (_, _) =>
            Task.FromResult(
                new TranscriptionResult(
                    [new Segment(1.0, 9.0, "hello there you")],
                    [
                        new Word("hello", 1.0, 2.0),
                        new Word("there", 6.0, 7.0),
                        new Word("you", 8.0, 9.0),
                    ]
                )
            );

        // The third sub-window starts 5 s into the window: its 1.5 s is the
        // window's 6.5 s.
        _rig.HearPriority = PriorityScript.On(
            2,
            new Word(" Fucking,", 1.5, 1.9),
            new Word("hello", 1.0, 1.2)
        );

        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        _heard = _sut.TranscribeWindowAsync(1, TestContext.Current.CancellationToken).Result;

        _rig.Listeners.ShouldHaveSingleItem();
        _rig.PriorityListeners.ShouldHaveSingleItem();
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void HearsTheWholeWindowOnceWithThePrimaryListener() =>
        _rig.Listeners[0].Heard.ShouldHaveSingleItem();

    [Fact]
    public void HearsEverySubWindowThatCanAddToTheWindowsShare() =>
        _rig.PriorityListeners[0].Heard.Count.ShouldBe(_subWindows.Count);

    [Fact]
    public void LeavesOutTheSubWindowsWhoseWordsTheWindowWouldThrowAway() =>
        _rig.PriorityListeners[0].Heard.Count.ShouldBe(8);

    [Fact]
    public void HandsItFiveSecondsAndSomeSilenceEachTime() =>
        _rig.PriorityListeners[0]
            .Heard.Select(samples => samples.Length)
            .Distinct()
            .ShouldBe([(int)((5.0 + WindowedAudio.TrailingSilenceSeconds) * 16000)]);

    [Fact]
    public void EndsEverySubWindowInSilence() =>
        _rig.PriorityListeners[0]
            .Heard.Select(samples => samples[samples.Length - 1])
            .ShouldAllBe(last => last == 0f);

    /// <summary>Window 1 starts 22 s in; its first sub-window is the window's own first samples.</summary>
    [Fact]
    public void StartsTheFirstSubWindowWithTheWindow() =>
        _rig.PriorityListeners[0].Heard[0][0].ShouldBe(_rig.Listeners[0].Heard[0][0]);

    [Fact]
    public void StartsEachSubWindowAtItsOwnPlaceInTheWindow() =>
        _rig.PriorityListeners[0]
            .Heard.Select(samples => samples[0])
            .ShouldBe(
                _subWindows.Select(sub =>
                    _rig.Listeners[0].Heard[0][(int)Math.Round(sub.Start * 16000)]
                )
            );

    [Fact]
    public void HearsEverySubWindowWithTheLaneHeld() =>
        _rig.PriorityListeners[0].LaneHeldWhileHearing.ShouldAllBe(held => held);

    [Fact]
    public void AddsThePriorityWordOnlyTheSubWindowsHeard() =>
        _heard.Words.Select(word => word.Text).ShouldBe(["hello", "there", " Fucking,", "you"]);

    [Fact]
    public void PutsItOnTheWindowsTimeline() =>
        _heard.Words[2].ShouldBe(new Word(" Fucking,", 6.5, 6.9));

    [Fact]
    public void LeavesTheSegmentsAsThePrimaryListenerHeardThem() =>
        _heard.Segments.Select(segment => segment.Text).ShouldBe(["hello there you"]);

    [Fact]
    public void LeavesTheLaneFreeAfterwards() => _rig.Lane.IsHeld.ShouldBeFalse();

    [Fact]
    public void KeepsBothListenersForTheNextWindow() =>
        (_rig.Listeners[0].Disposed || _rig.PriorityListeners[0].Disposed).ShouldBeFalse();
}

/// <summary>
/// A priority word the primary listener already heard, at the same time, is
/// not heard twice.
/// </summary>
public sealed class WhenThePrimaryListenerAlreadyHeardThePriorityWord : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _primary = new(
        [new Segment(1.0, 7.0, "oh fuck")],
        [new Word("oh", 1.0, 2.0), new Word("fuck", 6.4, 6.8)]
    );
    private readonly TranscriptionResult _heard;

    public WhenThePrimaryListenerAlreadyHeardThePriorityWord()
    {
        _rig.Hear = (_, _) => Task.FromResult(_primary);
        _rig.HearPriority = PriorityScript.On(2, new Word("Fuck!", 1.5, 1.9));

        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        _heard = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void ReturnsWhatThePrimaryListenerHeard() => _heard.ShouldBeSameAs(_primary);
}

/// <summary>
/// The prompted listener loses ordinary words and may mishear them, so nothing
/// but a priority word is ever taken from it - nor anything it says about the
/// silence after a sub-window.
/// </summary>
public sealed class WhenTheSubWindowsHearNoPriorityWord : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _heard;

    public WhenTheSubWindowsHearNoPriorityWord()
    {
        _rig.HearPriority = (_, _) =>
            Task.FromResult(
                new TranscriptionResult(
                    [new Segment(0.0, 5.4, "damn it fuck")],
                    [
                        new Word("damn", 1.0, 1.4),
                        new Word("it", 1.4, 1.6),
                        new Word("fuck", 5.1, 5.4),
                    ]
                )
            );

        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        _heard = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void AddsNothing() => _heard.Words.Select(word => word.Text).ShouldBe(["heard"]);
}

/// <summary>
/// A file shorter than one sub-window is re-heard whole, once: the short
/// clip is exactly what the prompted listener hears best.
/// </summary>
public sealed class WhenTheAudioIsShorterThanOneSubWindow : IDisposable
{
    private readonly WindowedAudioRig _rig = new(seconds: 3.0);
    private readonly WindowedAudio _sut;
    private readonly TranscriptionResult _heard;

    public WhenTheAudioIsShorterThanOneSubWindow()
    {
        _rig.HearPriority = PriorityScript.On(0, new Word("fucked", 2.0, 2.4));

        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        _heard = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void HearsTheWholeFileOnceMoreAndSomeSilence() =>
        _rig.PriorityListeners[0]
            .Heard.ShouldHaveSingleItem()
            .Length.ShouldBe((int)((3.0 + WindowedAudio.TrailingSilenceSeconds) * 16000));

    [Fact]
    public void AddsWhatItHeard() => _heard.Words.ShouldContain(new Word("fucked", 2.0, 2.4));
}

/// <summary>
/// With no Priority Word Pass, or an empty list, a window is heard exactly as
/// it always was and no prompted listener - no second whisper state - is built.
/// </summary>
public sealed class WhenThePriorityWordListIsEmpty : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;

    public WhenThePriorityWordListIsEmpty()
    {
        _sut = _rig.Open(priorityWords: PriorityWordList.FromLines([]));
        _ = _sut.TranscribeWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.DisposeAsync().AsTask().Wait();
        _rig.Dispose();
    }

    [Fact]
    public void BuildsNoPromptedListener() => _rig.PriorityListeners.ShouldBeEmpty();

    [Fact]
    public void StillHearsTheWindow() => _rig.Listeners[0].Heard.ShouldHaveSingleItem();
}

/// <summary>
/// Both listeners are built once for the audio, on its first window, and
/// reused - the prompted one holds whisper state too.
/// </summary>
public sealed class WhenEveryWindowIsHeardWithThePriorityWordPass : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;

    public WhenEveryWindowIsHeardWithThePriorityWordPass()
    {
        _sut = _rig.Open(priorityWords: PriorityWordList.Default);

        _rig.PriorityListeners.ShouldBeEmpty();

        for (var index = 0; index < _sut.Windows.Count; index++)
        {
            _ = _sut.TranscribeWindowAsync(index, TestContext.Current.CancellationToken).Result;
        }

        _sut.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void BuildsOnePromptedListenerForAllOfThem() =>
        _rig.PriorityListeners.ShouldHaveSingleItem();

    [Fact]
    public void DisposesItWhenTheAudioCloses() => _rig.PriorityListeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void DisposesThePrimaryListenerToo() => _rig.Listeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void TellsTheEngineItClosedOnce() => _rig.Closed.ShouldBe(1);
}

/// <summary>
/// Each inference takes its own turn in the lane, so a Watch window waiting at
/// High gets the GPU between a Job window's primary hearing and its
/// sub-windows, not after all twelve of them: a running Job still delays a
/// viewer by at most one inference.
/// </summary>
public sealed class WhenAWatchWindowArrivesWhileAJobWindowIsBeingHeard : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly List<string> _order = [];

    public WhenAWatchWindowArrivesWhileAJobWindowIsBeingHeard()
    {
        var job = _rig.Open(InferencePriority.Normal, PriorityWordList.Default);
        var watch = _rig.Open(InferencePriority.High);

        var jobHearing = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var jobMayFinish = new TaskCompletionSource<TranscriptionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _rig.Hear = (samples, _) =>
        {
            // Window 0 starts at frame 0, window 1 does not.
            if (samples[0] == 0f)
            {
                _order.Add("job");
                jobHearing.TrySetResult();
                return jobMayFinish.Task;
            }

            _order.Add("watch");
            return Task.FromResult(new TranscriptionResult([], []));
        };
        _rig.HearPriority = (_, _) =>
        {
            _order.Add("job sub-window");
            return Task.FromResult(new TranscriptionResult([], []));
        };

        var jobWindow = Task.Run(() =>
            job.TranscribeWindowAsync(0, TestContext.Current.CancellationToken)
        );
        jobHearing.Task.Wait(TestContext.Current.CancellationToken);

        var watchWindow = Task.Run(() =>
            watch.TranscribeWindowAsync(1, TestContext.Current.CancellationToken)
        );
        SpinWait.SpinUntil(() => _rig.Lane.Waiting == 1, TimeSpan.FromSeconds(10));

        jobMayFinish.SetResult(new TranscriptionResult([], []));
        Task.WhenAll(jobWindow, watchWindow).Wait(TestContext.Current.CancellationToken);
        job.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
        watch.DisposeAsync().AsTask().Wait(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void HearsTheWatchWindowBeforeTheJobsSubWindows() =>
        _order.Take(3).ShouldBe(["job", "watch", "job sub-window"]);
}

/// <summary>
/// A window cancelled while a sub-window is on the GPU: the prompted listener
/// is thrown away, and waited for, before the lane is let go - the same rule
/// as for the primary one - and the next window builds a fresh one.
/// </summary>
public sealed class WhenAWindowIsCancelledWhileASubWindowIsHeard : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly Exception? _thrown;

    public WhenAWindowIsCancelledWhileASubWindowIsHeard()
    {
        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        var hearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.HearPriority = async (_, cancellationToken) =>
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

        _rig.HearPriority = null;
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
    public void DisposesTheInterruptedPromptedListener() =>
        _rig.PriorityListeners[0].Disposed.ShouldBeTrue();

    [Fact]
    public void DisposesItBeforeLettingGoOfTheLane() =>
        _rig.PriorityListeners[0].LaneHeldWhenDisposed.ShouldBeTrue();

    [Fact]
    public void KeepsThePrimaryListener() => _rig.Listeners[0].Disposed.ShouldBeFalse();

    [Fact]
    public void LeavesTheLaneFree() => _rig.Lane.IsHeld.ShouldBeFalse();

    [Fact]
    public async Task HearsTheNextWindowWithAFreshPromptedListener()
    {
        await _sut.TranscribeWindowAsync(1, TestContext.Current.CancellationToken);

        _rig.PriorityListeners.Count.ShouldBe(2);
    }
}

/// <summary>
/// A cancellation that arrives between two sub-windows stops the window at the
/// next turn; nothing is disposed, because nothing was on the GPU.
/// </summary>
public sealed class WhenAWindowIsCancelledBetweenSubWindows : IDisposable
{
    private readonly WindowedAudioRig _rig = new();
    private readonly WindowedAudio _sut;
    private readonly Exception? _thrown;

    public WhenAWindowIsCancelledBetweenSubWindows()
    {
        _sut = _rig.Open(priorityWords: PriorityWordList.Default);
        using var abandon = new CancellationTokenSource();
        _rig.HearPriority = (_, _) =>
        {
            abandon.Cancel();
            return Task.FromResult(new TranscriptionResult([], []));
        };

        _thrown = Record.Exception(() =>
            _sut.TranscribeWindowAsync(0, abandon.Token).GetAwaiter().GetResult()
        );
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
    public void HearsNoFurtherSubWindow() => _rig.PriorityListeners[0].Heard.ShouldHaveSingleItem();

    [Fact]
    public void LeavesTheLaneFree() => _rig.Lane.IsHeld.ShouldBeFalse();
}

public sealed class WhenAPriorityPassIsBuiltWithoutCollaborators
{
    [Fact]
    public void RefusesAMissingList() =>
        Should.Throw<ArgumentNullException>(() =>
            new PriorityPass(null!, () => new FakeListener(new InferenceLane()))
        );

    [Fact]
    public void RefusesNoWayToListen() =>
        Should.Throw<ArgumentNullException>(() =>
            new PriorityPass(PriorityWordList.Default, null!)
        );
}
