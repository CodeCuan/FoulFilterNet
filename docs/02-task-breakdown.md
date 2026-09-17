# 02 — Task Breakdown

Derived from [01-python-analysis.md](01-python-analysis.md). Every task is one
commit, TDD, tests green before it lands.

> **Amended by [03-parallelisation-review.md](03-parallelisation-review.md).**
> That review recommends two changes to T01 and T02 — scaffold all sixteen
> projects up front, and hoist the interface set into T02 — which take T21 off
> the Smart Cut critical path. Read it before starting T01.

---

## Solution layout

```
FoulFilterNet.slnx
├── src/
│   ├── FoulFilterNet.Domain            pure artifacts + rules, zero I/O
│   ├── FoulFilterNet.Media             FFmpeg/FFprobe: graphs (pure) + process adapter
│   ├── FoulFilterNet.Transcription     ITranscriber / IAligner + Whisper.net engine
│   ├── FoulFilterNet.SmartCut          ISmartCutAdvisor + LLM transports
│   ├── FoulFilterNet.Pipeline          orchestration + transcript cache
│   ├── FoulFilterNet.Jobs              queue, worker, event fan-out
│   ├── FoulFilterNet.Web               minimal API + wwwroot
│   └── FoulFilterNet.Cli               find_and_remove equivalent
└── tests/
    └── FoulFilterNet.<Name>.Tests      one per src project
```

Reference direction is strictly inward. `Domain` references nothing.
`Media`, `Transcription` and `SmartCut` reference only `Domain`. `Pipeline`
references those four and depends on their **interfaces** only, so the whole
pipeline is exercised in unit tests with stubs — the same property the Python
version has today and the reason its tests never load a model.

## Cross-cutting conventions

- `net10.0`, nullable enabled, `TreatWarningsAsErrors`, central package
  management via `Directory.Packages.props`.
- xUnit v3 + Shouldly + NSubstitute. Test classes named for the scenario
  (`WhenMergingOverlappingHits`), SUT and shared asserts in the constructor.
- Seconds as `double` throughout, matching Python and FFmpeg syntax.

### One trap worth naming up front

Python renders the silence filter with `repr(float)` — `1.0` becomes `"1.0"`,
`2.7` becomes `"2.7"` — while the bleep and remove graphs use explicit `.3f`.
C#'s default `double.ToString()` renders `1.0` as `"1"`. Getting the
filtergraphs byte-identical to the Python (and to the ported tests) needs a
deliberate formatter, not `ToString()`. This is called out in T07 because it is
silent when wrong: FFmpeg accepts both spellings, so only a string comparison
catches it.

---

## Phase 0 — Foundation

### T01 · Solution skeleton and conventions
Create `FoulFilterNet.slnx`, `Directory.Build.props`, `Directory.Packages.props`,
`.editorconfig`, `.gitattributes`. Replace the Python CI workflow with a
`dotnet build` + `dotnet test` one.
**Done when:** `dotnet test` runs and reports zero tests without error.
**Depends on:** —

### T02 · Domain artifacts
`Segment`, `Word`, `Candidate`, `Hit`, `Transcript` as records; `CensorMethod`
and `MediaKind` enums; a `Times` helper owning the 3-decimal rounding.
**Tests:** rounding behaviour and value equality.
**Depends on:** T01

---

## Phase 1 — Pure core

The direct ports. Every test here has a Python counterpart in
[test_pipeline_logic.py](../Legacy/src/test_pipeline_logic.py) — port the
assertions first, then write the implementation.

### T03 · Tokenizer and Bad Words List
`[a-z0-9']+` over lowercased text. List normalization: trim, drop blanks and
`#` comments, tokenize, keep entries of 1–3 tokens, reject the rest. Empty list
is an error.
**Tests:** comments and blanks skipped; apostrophes survive; over-long entries
dropped; empty list throws.
**Depends on:** T02

### T04 · Phrase matching
N-grams longest-first, then dedupe by `(start, -length)` dropping anything
fully contained in a kept match. `FindCandidates` over segment text with
character-offset time interpolation; `FindHits` over aligned words with exact
times.
**Tests:** case and punctuation insensitivity; multi-word phrases; no substring
false positives ("classify the class" vs `ass`); longest phrase wins; word
spans correct.
**Depends on:** T03

### T05 · Hit padding and merging
0.15 s pre, 0.25 s post; drop inverted windows; clamp start at zero; round to
3 dp; sort; merge overlaps concatenating phrases with `+`.
**Tests:** touching windows collapse to one; negative start clamps; inverted
dropped; order preserved.
**Depends on:** T02

### T06 · Smart Cut index mapping
`-1/-1` means reject. When widening is not permitted, collapse both indices to
the target. Clamp into range, then map to `cut_start`/`cut_end`.
**Tests:** widening maps the phrase span; surgical mode clamps to the target;
`-1` rejects; out-of-bounds indices clamp.
**Depends on:** T02

---

## Phase 2 — Media

### T07 · Filtergraph builders
Pure string construction for `silence`, `bleep` and `remove`; all three throw
on an empty hit list; `remove` throws when hits would consume the whole file.
**Tests:** the exact strings, ported character-for-character from the Python
suite. **Includes the float-formatting work described above** — the tests are
the only thing that will catch it.
**Depends on:** T02

### T08 · FFmpeg process adapter
`IFFmpegRunner` over `System.Diagnostics.Process`, plus argument construction
kept separate and pure. Non-zero exit surfaces stderr.
**Tests:** argument lists; exit-code handling against a fake process.
**Depends on:** T01

### T09 · Media probing
FFprobe JSON: duration, audio sample rate, and presence of a video stream to
decide `MediaKind`. Replaces libmagic (finding 7). The `.m4b`/`.m4a` extension
short-circuit is no longer needed but the *outcome* for those files must not
change.
**Tests:** parse captured ffprobe JSON fixtures for audio, video, and an
audiobook `.m4b`; unrecognized input yields `Unknown`.
**Depends on:** T08

### T10 · Audio preparation
Extract the audio track from a video; pad the start by 4 s for the Rescan Pass;
crop a mono 16 kHz span.
**Tests:** argument construction for each, against a fake runner.
**Depends on:** T08

### T11 · Media editor
Compose graphs and runner into `CensorAudio` / `CensorVideo`, including the
bleep-on-video path (render the track, then mux with `-c:v copy`) and the
`remove`-on-video fallback to `silence`. Default output naming.
**Tests:** with a fake runner, assert the command sequence per method and media
kind, and that the temp bleep track is cleaned up.
**Depends on:** T07, T08, T09

---

## Phase 3 — Transcription

### T12 · Transcription abstractions and Rescan shifting
`ITranscriber`, `IAligner`, `TranscriptionOptions`. Model-name normalization
(`base` becomes the full model id, a slash-qualified name passes through).
The Rescan Pass timestamp arithmetic — subtract the offset, drop segments that
end at or before zero, clamp starts, round — is pure and belongs here.
**Tests:** name normalization; shifted-segment arithmetic; segment union
dropping duplicate overlapping text.
**Depends on:** T02

### T13 · Whisper.net transcriber
The concrete engine: model acquisition, **CUDA** runtime selection for the
RTX 3080 Ti with CPU fallback, word-level timestamps, and the
`UNLOAD_MODELS_AFTER_JOB` lifetime. Write the new ADR recording the departure
from ADR-0001 here.
**Tests:** device/option selection is unit-tested; an opt-in integration test
(skipped by default, mirroring `RUN_LIVE_LLM_TESTS`) transcribes
`single_hit.mp3` and asserts the word "damn" lands within tolerance of the
manifest's 3.410–3.897 s ground truth. That tolerance *is* the answer to
"is DTW precise enough?" — record the measured error in the ADR.
**Depends on:** T12, T10

### T14 · Aligner seam
`IAligner` plus a pass-through implementation for the case where the
transcriber already returned words. Keeps ADR-0001's stage in the design
without a wav2vec2 dependency.
**Tests:** pass-through returns the transcriber's words untouched; pipeline
treats a null aligner and a pass-through aligner identically.
**Depends on:** T12

---

## Phase 4 — Smart Cut

### T15 · Prompt construction
Port `PROMPT_TEMPLATE` and its eight worked examples verbatim; append the
surgical rule only when widening is forbidden; render the indexed context
window.
**Tests:** surgical rule present/absent as expected; indices rendered from zero;
target phrase interpolated.
**Depends on:** T02

### T16 · Response parsing
Extract the first JSON object from a possibly-chatty response; `NONE` means
reject; `API_UNAVAILABLE` / `ERROR_OR_REFUSAL` / empty mean *keep original
timestamps*; malformed JSON degrades to the same.
**Tests:** each branch, including JSON wrapped in prose and in code fences.
**Depends on:** T06

### T17 · LLM transports
Gemini client (temperature 0.1, 300 output tokens, JSON mime type, safety
categories off, exponential backoff on 429/503) and an OpenAI-compatible client
including the self-healing `/models` retry when the configured model id is
rejected.
**Tests:** against a fake `HttpMessageHandler` — request shape, backoff
sequence, the model-discovery retry, and that transport failure returns
"unavailable" rather than throwing.
**Depends on:** T16

### T18 · Smart Cut advisor
Compose prompt, transport and mapping behind `ISmartCutAdvisor`.
**Feature-flagged via `SmartCut:Enabled` in `appsettings.json`, default
`false`** — see the flag block in
[00-porting-plan.md](00-porting-plan.md#feature-flags). When disabled, DI
resolves a no-op advisor so the pipeline needs no conditional; transport is
chosen from `SmartCut:Mode`. Enabled-but-unconfigured (no key, no local server)
degrades to the no-op with a warning rather than failing a job. The Google API
key comes from environment or user-secrets, never from committed config.
**Tests:** default configuration yields the no-op advisor; the flag on with each
mode yields the right transport; enabled-without-credentials degrades rather
than throws; an advisor exception never escapes into the pipeline.
**Depends on:** T15, T17

---

## Phase 5 — Pipeline

### T19 · Transcript store
MD5 content hash, `{digest}_{name}.json` naming, cache lookup, and **schema
version validation** so a stale-version transcript is a miss (finding 4).
**Tests:** round-trip; hash stability; version mismatch is a miss; corrupt JSON
is a miss, not a crash.
**Depends on:** T02

### T20 · Candidate/Hit reconciliation
Confirm Hits from aligned words; for Candidates alignment did not recover, fall
back to the segment estimate. **Reconcile by time proximity, not phrase string**
(finding 2), so repeated occurrences of the same word each get their own
fallback.
**Tests:** a phrase occurring five times with one aligned yields five hits —
the case the Python version gets wrong.
**Depends on:** T04, T05

### T21 · Pipeline orchestrator
The spine: prepare, transcribe or resume, optional rescan, match, align,
persist, reconcile, merge, optional Smart Cut, optional debug dump, render.
Progress reported at the documented percentages; cancellation observed at every
checkpoint; widening permitted only for `remove` on audio (ADR-0004); no hits
means copy the input through.
**Tests:** the large one — stubbed transcriber, aligner, advisor and editor.
Stage order and percentages; resume skips transcription; rescan forces it;
cancellation at each checkpoint; widening flag per method and media kind;
advisor rejection drops a hit and triggers a re-merge; render dispatch per
media kind.
**Depends on:** T11, T14, T18, T19, T20

### T22 · Model release policy
`UNLOAD_MODELS_AFTER_JOB` honoured after every job, success or failure.
**Tests:** release called on the failure path too; disabled flag skips it.
**Depends on:** T21

---

## Phase 6 — Jobs and Web

### T23 · Job queue and worker
`JobRecord` and status model; `Channel<T>` plus a `BackgroundService` consumer
with a blocking read (finding 6); strictly one job at a time; per-job
`CancellationTokenSource` (finding 5) covering both queued and running
cancellation.
**Tests:** sequential execution under concurrent enqueues; cancel-while-queued
never starts; cancel-while-running propagates; failure isolates to one job.
**Depends on:** T02

### T24 · Job event fan-out
Subscribe/unsubscribe, snapshot on connect, and a slow subscriber that must not
stall the worker.
**Tests:** subscriber receives updates in order; a dead subscriber is dropped
without blocking; unsubscribe is idempotent.
**Depends on:** T23

### T25 · Upload handling
Filename sanitization (basename only, lowercased extension, colon to " -",
brackets and quotes stripped, non-empty fallback), extension allow-list, and
the streaming size cap.
**Tests:** ported sanitization cases including traversal and the empty/dot
inputs; oversize upload rejected and the partial file removed.
**Depends on:** T01

### T26 · HTTP endpoints
`/upload`, `/jobs`, `/status/{id}`, `DELETE /jobs/{id}`, `/download/{id}`,
`/config`. Path fields never leave the process.
**Tests:** integration via `WebApplicationFactory` against a stub pipeline.
**Depends on:** T23, T25

### T27 · Server-sent events
`/events` using `TypedResults.ServerSentEvents`, with the retry hint and
keepalive the client expects.
**Tests:** integration — a job update reaches a connected client.
**Depends on:** T24, T26

### T28 · Zip download
`/download_zip?ids=`, streaming, **without the duplicated `censored_` prefix**
(finding 3).
**Tests:** entry names; only completed jobs included; empty selection is a 404.
**Depends on:** T26

### T29 · Front end
Copy `index.html`, `app.css`, `app.js` to `wwwroot` unchanged; serve `/` and
static files; wire `/config`. **Smart Cut is actually connected in this task**
(finding 1), and `/config` reports `SmartCut:Enabled` so the "Smart Cut active"
badge reflects reality — with the flag defaulting off, the badge is hidden out
of the box.
**Tests:** integration — the page is served; `/config` shows the flag off by
default and on when configured; an end-to-end run with the flag enabled reaches
the advisor, and with it disabled does not.
**Depends on:** T18, T26

### T30 · Startup housekeeping
Create the data directories; wipe `uploads/` and `scratch/` on boot; leave
`transcripts/` and `outputs/` alone (ADR-0002).
**Tests:** against a temp root — scratch cleared, transcripts survive.
**Depends on:** T26

---

## Phase 7 — Entry point and packaging

### T31 · CLI
`System.CommandLine` port of `find_and_remove.py`, including flag precedence
(`--censor_method` beats `--bleep` beats `--delete` beats the env default) and
the legacy `delete` synonym for `remove`.
**Tests:** argument-to-options mapping across the precedence matrix; `--no_edit`
reports without writing.
**Depends on:** T21

### T32 · Container and configuration
`aspnet:10.0` base plus FFmpeg and the whisper.cpp **CUDA** runtime; compose
file using the NVIDIA device reservation rather than the ROCm `/dev/kfd`
mapping; `appsettings.json` as the primary configuration source with
environment overrides retaining the legacy variable names.
**Done when:** the app runs natively on Windows *and* the image builds and
serves the UI. Native-first: containerised GPU access on Windows needs WSL2 and
the NVIDIA container toolkit, and that should be a deployment choice, not a
prerequisite for running the thing.
**Depends on:** T29, T31
*Outcome: the configuration half shipped. The container files were written but
never built, then deleted - the app runs natively on the target machine with
direct CUDA access. See STATUS, "Container dropped".*

### T33 · Documentation
New ADR for the ASR change, a `CONTEXT.md` carried forward, and a README for
the .NET solution.
**Depends on:** T13, T32

### T34 · Evaluation harness *(stretch)*
Port `eval_misses.py` — score recall, precision and boundary error against
`tests/fixtures/media/manifest.json`, whose spans are exact by construction.
This is what turns "is Whisper.net's DTW precise enough?" from an opinion into
a number. Most of the splicing work the Python script did is already done by
the fixture generator; this task is the scoring half.
**Depends on:** T21

---

## Summary

34 tasks, 34 commits, eight production projects and eight test projects.
Phases 1 and 2 carry the bulk of the ported test assertions and should feel
mechanical; T21 is the single largest piece of new design work; T13 carries the
only real technical risk.
