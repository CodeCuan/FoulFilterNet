using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// Opened analysis audio with no GPU behind it: each window hears what the
/// script says, and the double records which windows were asked for, with
/// which token, and whether it was closed.
/// </summary>
internal sealed class ScriptedAudio : IAnalysisAudio
{
    private readonly Func<int, CancellationToken, TranscriptionResult> _hear;

    public ScriptedAudio(double durationSeconds, Func<int, TranscriptionResult> hear)
        : this(durationSeconds, (index, _) => hear(index)) { }

    public ScriptedAudio(
        double durationSeconds,
        Func<int, CancellationToken, TranscriptionResult> hear
    )
    {
        DurationSeconds = durationSeconds;
        Windows = TranscriptionWindows.Plan(durationSeconds);
        _hear = hear;
    }

    public double DurationSeconds { get; }

    public IReadOnlyList<TranscriptionWindow> Windows { get; }

    public List<int> Asked { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public int Disposals { get; private set; }

    /// <summary>Called as the audio is closed, before anything else sees it closed.</summary>
    public Action? OnDispose { get; set; }

    public Task<TranscriptionResult> TranscribeWindowAsync(
        int index,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Disposals > 0, this);
        Asked.Add(index);
        Tokens.Add(cancellationToken);
        return Task.FromResult(_hear(index, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        OnDispose?.Invoke();
        Disposals++;
        return ValueTask.CompletedTask;
    }

    /// <summary>A result holding one word and one segment at a window-local time.</summary>
    public static TranscriptionResult Said(string word, double start, double end) =>
        new([new Segment(start, end, word)], [new Word(word, start, end)]);
}
