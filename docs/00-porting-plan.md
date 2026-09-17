# 00 — Porting Plan

Status: **initial plan**, written before analysis. Target architecture is
refined by [01-python-analysis.md](01-python-analysis.md) and turned into
work items by [02-task-breakdown.md](02-task-breakdown.md).

## Goal

Recreate the FoulFilter application on .NET 10, preserving observable
behaviour: the same detection semantics, the same FFmpeg edits, the same HTTP
surface, and the same domain vocabulary. The Python tree under `/Legacy` is the
reference implementation and is not modified.

## Target environment

| | |
|---|---|
| **GPU** | NVIDIA GeForce RTX 3080 Ti — 12 GB GDDR6X, Ampere (SM 8.6), driver 595.79 |
| **Platform** | Windows 11; Docker Desktop 27.4.0 with Compose v2.31.0 |
| **SDK** | .NET 10.0.400 |
| **FFmpeg** | 7.1.1 (full build) on `PATH` |

This settles several open questions in one go. The legacy stack's whole
awkward shape — the hybrid ASR of ADR-0001, `HSA_OVERRIDE_GFX_VERSION`, the
ROCm wheel index, the `WHISPER_MULTI_GPU` sharding flag — exists to work around
**AMD RDNA2 on ROCm**. None of it applies here: CUDA is the best-supported
backend in every ASR runtime worth considering.

12 GB of VRAM comfortably holds `large-v3-turbo` (~1.6 GB in FP16) with room to
spare, so model size is not a constraint. `UNLOAD_MODELS_AFTER_JOB` keeps its
value for a different reason than the original — sharing the card with a local
LLM for Smart Cut, or with anything else on a desktop machine.

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

## Feature flags

Optional behaviour is configured in
[`src/FoulFilterNet.Web/appsettings.json`](../src/FoulFilterNet.Web/appsettings.json),
bound through `IOptions<T>`, and **off by default**:

```jsonc
{
  "SmartCut": {
    "Enabled": false,          // master switch — off unless explicitly turned on
    "Mode": "Local",           // Local | Google
    "LocalUrl": "http://localhost:8080/v1/chat/completions",
    "LocalModel": "",          // empty asks the server what it is running
    "GoogleModel": "gemini-2.5-flash-lite",
    "ContextRadius": 11        // words either side of the target word
  },
  "Transcription": {
    "Model": "base",           // a bare size expands to openai/whisper-base
    "Language": "",            // empty means detect
    "Device": "Auto",          // Auto | Cuda | Cpu
    "ModelDirectory": "models", // GGML weights; the image sets /data/models
    "UnloadAfterJob": false    // UNLOAD_MODELS_AFTER_JOB
  },
  "Storage": {
    "DataDirectory": "",       // blank: %LOCALAPPDATA%\FoulFilterNet; the image sets /data
    "TranscriptDirectory": "", // blank: <DataDirectory>/transcripts
    "BadWordsPath": "",        // blank: <DataDirectory>/bad_words.txt
    "MaxUploadMegabytes": 4096
  },
  "Cli": {
    "CensorMethod": "silence"  // the CLI's default when no flag names one
  }
}
```

Every value above is the code default, written out so the flags are
*discoverable* rather than only inferable from source. The `Storage` paths are
written **blank**, meaning "the default": that default is a per-user folder
computed on each machine, so any literal path committed here would state
something false about where the app writes (T32). The CLI reads this same file,
copied beside its executable.

Environment variables override the file under their standard `Section__Key`
names **and** under the Python's names (`DATA_DIR`, `WHISPER_MODEL`,
`AI_ENHANCE`, ...), which `LegacyEnvironmentVariables` in
`FoulFilterNet.Pipeline` maps onto these keys for both hosts. The full table is
in the T32 output section of [STATUS.md](STATUS.md).

Smart Cut stays off unless someone opts in, for three reasons: it costs an LLM
round trip per Hit, it is the only stage that can *change* what gets cut rather
than just where, and it needs an API key or a local server that may not be
running. A disabled advisor is a no-op in the pipeline — not a failure — and
`GET /config` must report the flag honestly so the UI badge means something
(see finding 1 in the analysis, where the Python advertises a feature it never
runs). The Google API key is *not* read from `appsettings.json` or any other
configuration source; it comes from the `GOOGLE_API_KEY` environment variable
only, and `appsettings.*.local.json` is gitignored. (The plan originally also
named user-secrets. They were never wired, and the configuration lookup meant
for them turned out to read committed JSON too, so it was removed - see STATUS,
"API key hardening".)

## Known open decision

The Python stack (HF Transformers Whisper + WhisperX wav2vec2 forced aligner)
has no direct .NET equivalent. This is the one place the port cannot be a
transliteration, and it is resolved in the analysis document. The pipeline is
designed against `ITranscriber`/`IAligner` so the engine choice stays swappable
whatever is picked.

## Test media

[`tests/fixtures/media`](../tests/fixtures/media) holds seven generated
fixtures (~1 MB) covering clean audio, a single hit, a multi-word phrase, five
repeats of one word, a false positive, a video, and an `.m4b` audiobook.

They are synthesised by
[`generate-fixtures.ps1`](../tests/fixtures/generate-fixtures.ps1) rather than
downloaded, which buys something no real-world sample can: **each profanity is
its own concatenated part, so its start and end are exact by construction**.
`manifest.json` carries that ground truth, making both detection recall and
boundary error measurable rather than eyeballed — the same trick the legacy
`eval_misses.py` used by splicing clips at known offsets.

## Definition of done (per task)

- Tests written first, and failing for the right reason.
- `dotnet test` green across the solution.
- `dotnet build` clean with warnings as errors.
- One commit, message describing behaviour rather than files touched.
