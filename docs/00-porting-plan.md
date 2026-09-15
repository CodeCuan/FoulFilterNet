# 00 — Porting Plan

Status: **initial plan**, written before analysis. Target architecture is
refined by [01-python-analysis.md](01-python-analysis.md) and turned into
work items by [02-task-breakdown.md](02-task-breakdown.md).

## Goal

Recreate the FoulFilter application on .NET 10, preserving observable
behaviour: the same detection semantics, the same FFmpeg edits, the same HTTP
surface, and the same domain vocabulary. The Python tree under `/Legacy` is the
reference implementation and is not modified.

## Ground rules

1. **TDD.** Every behavioural task starts with a failing test. Production code
   is written to make that test pass, then tidied. Tasks that cannot be
   meaningfully unit-tested (process launch, model loading) are isolated behind
   a thin adapter so that the logic around them still can be.
2. **Pure core, adapters at the edges.** Filtergraph strings, token matching,
   hit merging and index mapping are pure functions over plain data — they get
   dense unit coverage and never touch a disk, a socket, or a GPU.
3. **Many small projects.** One assembly per bounded concern, each with a
   matching `*.Tests` project. A project that cannot justify its own test
   project probably should not exist.
4. **Commit per stage.** Each task in the breakdown lands as its own commit
   with its tests green.
5. **No behavioural drift without a note.** Where .NET forces a different
   approach (ASR stack, media sniffing), the deviation is recorded as an ADR in
   `docs/adr/` rather than absorbed silently.

## Conventions

### Solution layout

```
FoulFilterNet.slnx
  src/    one folder per project
  tests/  one *.Tests project per src project
  docs/   these documents + ADRs
  Legacy/ untouched Python reference
```

### Test style

xUnit test classes follow a **constructor-as-arrange/act** shape, as requested:

```csharp
public class WhenMergingOverlappingHits
{
    private readonly IReadOnlyList<Hit> _merged;   // result under inspection

    public WhenMergingOverlappingHits()
    {
        var sut = new HitMerger(Padding.Default);          // SUT built here
        _merged = sut.Merge([Hit("x", 1.0, 1.5), Hit("x", 1.55, 2.0)]);

        _merged.ShouldNotBeNull();                          // common asserts
        _merged.ShouldAllBe(h => h.End > h.Start);          // shared by every
        _merged.ShouldBeInOrder(h => h.Start);              // fact below
    }

    [Fact] public void CollapsesToASingleWindow() => _merged.Count.ShouldBe(1);
    [Fact] public void StartsAtThePaddedFirstHit() => _merged[0].Start.ShouldBe(0.85, 0.001);
    [Fact] public void EndsAtThePaddedLastHit()   => _merged[0].End.ShouldBe(2.25, 0.001);
    [Fact] public void ConcatenatesPhrases()      => _merged[0].Phrase.ShouldContain("+");
}
```

The class name states the scenario; each `[Fact]` asserts exactly one thing and
needs no setup of its own. Invariants that every fact depends on are asserted
once in the constructor, so a broken precondition fails every fact in the class
with one obvious message instead of producing a cascade of unrelated failures.

Chosen libraries: **xUnit v3**, **Shouldly** for assertions, **NSubstitute** for
the few real seams (process runner, LLM transport, clock).

### Code conventions

- Nullable enabled, warnings as errors, `TreatWarningsAsErrors` on all projects.
- `record` types for pipeline artifacts (`Segment`, `Candidate`, `Word`, `Hit`) —
  they are values, compared by content, and freely shared across threads.
- Times are `double` seconds end to end, matching the Python and the FFmpeg
  filter syntax, rather than `TimeSpan`. Rounding to 3 decimals happens at the
  same boundaries it does today so filtergraph output stays byte-comparable.
- `Directory.Build.props` carries shared settings; individual csproj files stay
  near-empty.

## Target architecture (provisional)

Layered, dependencies pointing inward:

```
Cli ─┐                                   ┌─ Transcription (ASR + alignment)
     ├─> Pipeline ─> Domain <────────────┤
Web ─┘        └────> Media (FFmpeg)      └─ SmartCut (LLM)
```

- **Domain** — artifacts and pure rules: tokenizer, bad-words matching, hit
  padding/merging, smart-cut index mapping. No I/O whatsoever.
- **Media** — FFmpeg/FFprobe: filtergraph construction (pure, testable) and
  process invocation (adapter).
- **Transcription** — `ITranscriber` / `IAligner` plus the concrete ASR engine.
- **SmartCut** — `ISmartCutAdvisor` plus Gemini and OpenAI-compatible clients.
- **Pipeline** — orchestration and the transcript cache; depends only on
  interfaces so the whole pipeline is unit-testable with stubs, exactly as the
  Python version is today.
- **Web** — ASP.NET Core minimal API, job queue, SSE, static UI.
- **Cli** — the `find_and_remove.py` equivalent.

## Known open decision

The Python stack (HF Transformers Whisper + WhisperX wav2vec2 forced aligner)
has no direct .NET equivalent. This is the one place the port cannot be a
transliteration, and it is resolved in the analysis document. The pipeline is
designed against `ITranscriber`/`IAligner` so the engine choice stays swappable
whatever is picked.

## Definition of done (per task)

- Tests written first, and failing for the right reason.
- `dotnet test` green across the solution.
- `dotnet build` clean with warnings as errors.
- One commit, message describing behaviour rather than files touched.
