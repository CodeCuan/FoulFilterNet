# STATUS — Port Progress

**Single source of truth for where the port is.** Every agent updates this file
as part of the task it completes, in the same commit as the work. If you are
resuming this project cold, read this file first and trust it over memory.

Last updated: 2026-09-15

---

## Current state

| | |
|---|---|
| Phase | **Wave 1** — streams unblocked |
| Branch | `main` |
| Solution | `FoulFilterNet.slnx`, 8 production + 8 test projects, builds clean |
| Tests | 463 passing, 8 skipped (4 project placeholders + 4 opt-in live-LLM) |

## Conventions for agents

- **One task, one commit, one branch.** Branch name `task/T<nn>-<slug>`, e.g.
  `task/T03-tokenizer`. Branch from the latest `main`.
- **TDD.** The first commit-worthy artifact is a failing test. Do not write
  production code ahead of a test that demands it.
- **Test style.** SUT constructed in the test class constructor along with the
  asserts every fact in that class shares; each `[Fact]` then asserts one thing.
  See [00-porting-plan.md](00-porting-plan.md#test-style).
- **Do not edit shared files.** `FoulFilterNet.slnx`, `Directory.Build.props`
  and `Directory.Packages.props` are owned by T01 and pre-populated. If you need
  a package that is not declared, say so in your report — do not add it
  yourself, or six branches will conflict on the same file.
- **Do not change interfaces.** Contracts are frozen at the end of Wave 0. If
  one is wrong, report it; a contract change is its own task.
- **Update this file in your task's commit** — fill in your row, note anything
  the next person needs to know.
- **Never push to `upstream`** (its push URL is deliberately disabled). Push to
  `origin`.

## Execution policy: sequential from here

**One task at a time.** Concurrent agents exhausted the session token budget
once already (2026-09-16), killing three mid-task and costing a stream's worth
of uncommitted work. The parallelisation analysis in
[03-parallelisation-review.md](03-parallelisation-review.md) remains correct
about what *could* run concurrently, but throughput is limited by tokens, not
by the dependency graph - so the graph's value now is ordering freedom, not
fan-out.

Consequences for whoever picks this up:

- Run one task, verify it green, commit, merge, then start the next.
- Keep committing per task. That discipline is what made the last interruption
  cost one task instead of seven.
- The dependency graph still decides *order*; it no longer decides *batching*.

## Task ledger

Status: `—` not started · `WIP` in progress · `✅` merged to main · `⚠️` blocked

| Task | Description | Status | Branch | Commit | Notes |
|---|---|---|---|---|---|
| T01 | Solution skeleton + all 16 projects | ✅ | `main` | `a03fdd8` | Owns shared files; scaffolds everything |
| T02 | Domain artifacts **+ hoisted contracts** | ✅ | `main` | `3e17d99` | Interfaces FROZEN - see Contracts below |
| T03 | Tokenizer + Bad Words List | ✅ | `task/T03-tokenizer` | `42a51b8` | `Tokenizer`, `BadWordsList` |
| T04 | Phrase matching | ✅ | `task/T04-phrase-matching` | `eedacee` | `PhraseMatcher` |
| T05 | Hit padding + merging | ✅ | `task/T05-hit-merging` | `5c3f4fc` | `HitMerger`, `HitPadding` |
| T06 | Smart Cut index mapping | ✅ | `task/T06-smartcut-mapping` | `6299a12` | `SmartCutMapper`; T16 depends on this |
| T07 | Filtergraph builders | ✅ | `task/T07-filtergraphs` | `136165d` | `FilterGraph`; float formatting verified against the Python strings |
| T08 | FFmpeg process adapter | ✅ | `task/T08-ffmpeg-runner` | `d29fa68` | `FFmpegRunner`, `FFmpegProcess` |
| T09 | Media probing (FFprobe) | ✅ | `task/T09-media-probing` | `d72fa0e` | `FFprobeMediaProber` |
| T10 | Audio preparation | ✅ | `task/T10-audio-prep` | `fe52974` | `FFmpegAudioPreparer`; T13 depends on this |
| T11 | Media editor | ✅ | `task/T11-media-editor` | `c8abea0` | `MediaEditor`; branched from T10 |
| T12 | ASR contracts + rescan shifting | ✅ | `task/T12-transcription-contracts` | `a089442` | `ModelNames`, `TranscriptionOptions`, `RescanPass` |
| T13 | Whisper.net transcriber (CUDA) | — | | | Stream C; only real technical risk; see notes below |
| T14 | Aligner seam | ✅ | `task/T14-aligner-seam` | `ffe8d32` | `PassThroughAligner`; branched from T12 |
| T15 | Smart Cut prompt | ✅ | `task/T15-smartcut-prompt` | `b9ef3fa` | `SmartCutPrompt`; template ported verbatim |
| T16 | Smart Cut response parsing | ✅ | `task/T16-response-parsing` | `779024c` | `SmartCutResponseParser`, `SmartCutResponses` |
| T17 | LLM transports | ✅ | `task/T17-llm-transports` | `2f28211` | `ISmartCutTransport`, `GeminiTransport`, `OpenAiCompatibleTransport` |
| T18 | Smart Cut advisor (flag, default off) | ✅ | `task/T18-smartcut-advisor` | `f052d4f` | `LlmSmartCutAdvisor`, `NoOpSmartCutAdvisor`, `AddSmartCut`; see notes below |
| T19 | Transcript store | — | | | Stream F |
| T20 | Candidate/Hit reconciliation | — | | | Stream A→F; fixes finding 2 |
| T21 | Pipeline orchestrator | — | | | Stream F; convergence point |
| T22 | Model release policy | — | | | Stream F |
| T23 | Job queue + worker | — | | | Stream E |
| T24 | Job event fan-out | — | | | Stream E |
| T25 | Upload handling | — | | | Stream E |
| T26 | HTTP endpoints | — | | | Stream E |
| T27 | Server-sent events | — | | | Stream E |
| T28 | Zip download | — | | | Stream E; fixes finding 3 |
| T29 | Front end + Smart Cut wiring | — | | | Fixes finding 1 |
| T30 | Startup housekeeping | — | | | Stream E |
| T31 | CLI | — | | | |
| T32 | Container + configuration | — | | | |
| T33 | Documentation + ASR ADR | — | | | |
| T34 | Evaluation harness | — | | | Stretch |

## Completed outside the ledger

| What | Commit | Notes |
|---|---|---|
| Docs: plan, analysis, breakdown, parallelisation review | `cdcd2f8`…`3fc3e06` | |
| Media fixtures + generator | `6bdeda6` | 7 fixtures, exact ground truth in `manifest.json` |
| Root `.gitignore` + `.gitattributes` | `6bdeda6`, `a03fdd8` | |

## Decisions already made

1. **ASR: Whisper.net (whisper.cpp) on CUDA.** Replaces the HF Whisper +
   WhisperX hybrid. Collapses transcription and alignment; `IAligner` survives
   as a seam. Target is an RTX 3080 Ti, 12 GB.
2. **Smart Cut is feature-flagged off** via `SmartCut:Enabled` in
   `appsettings.json`. Disabled resolves a no-op advisor.
3. **Contracts hoisted into T02** so the orchestrator is not blocked by engine
   implementations — see
   [03-parallelisation-review.md](03-parallelisation-review.md).
4. **FFprobe replaces libmagic** for media type detection.
5. **Fixtures are generated, not downloaded**, so every profanity's span is
   exact by construction.

## Toolchain gotchas (learned the hard way in T01)

- **Never pass `--nologo` to `dotnet test`.** Under Microsoft.Testing.Platform,
  unrecognised arguments are forwarded to the test executable, which rejects
  them and reports "Zero tests ran" with exit code 5 rather than failing
  loudly. This looks exactly like a broken test discovery setup.
- **Run `dotnet test` from the repository root.** A relative project path
  resolved from elsewhere silently runs a stale assembly.
- **Transitive pinning is off** in `Directory.Packages.props`. With it on, the
  `xunit.v3` metapackage resolves but none of its assemblies reach the output.
- **The MTP opt-in is in `global.json`** (`"test": {"runner": ...}`), not
  `dotnet.config` and not an MSBuild property.

## Interrupted work (session usage limit, 2026-09-16)

Three agents were killed mid-task by a usage limit. Stream B's T07-T09 were
complete and committed, and are merged. Everything else was **discarded rather
than salvaged**: a TDD agent killed mid-cycle leaves state whose test-first
ordering cannot be verified after the fact, and re-running the task is cheaper
than auditing it. Specifically discarded, and safe to start clean:

- **T23** — a branch at `3e17d99` with seven uncommitted files in
  `FoulFilterNet.Jobs`.

## Notes for T13 (left by Stream C)

`FoulFilterNet.Transcription` now contains everything T13 needs that is not the
engine itself. Nothing in it loads a model, touches a GPU, or downloads
weights, and it must stay that way — CI is CPU-only.

- **`ModelNames.Normalize`** returns Hugging Face-style ids (`base` becomes
  `openai/whisper-base`) because that is what existing configuration and the
  Python carried. whisper.cpp wants a **GGML weights file**, so T13 owns the
  mapping from a repository id to `ggml-base.bin` and the decision about where
  it is cached. Keep the configuration spelling; map at the edge.
- **`RescanPass`** owns all the Rescan arithmetic. `TranscribeShiftedAsync`
  should pad the audio (T10's job), transcribe the padded file, and hand the
  raw result to `RescanPass.Shift(TranscriptionResult, offsetSeconds)` — which
  rebases segments *and* words in one call. Do not re-derive the arithmetic.
  `RescanPass.Union` is the pipeline's (T21) merge, not the transcriber's.
- **`TranscriptionOptions.UnloadAfterJob`** is the flag; honouring it is T13's
  `ReleaseAsync` and T22's policy.
- **`TranscriptionDevice.Auto`** means "CUDA if it is usable, else CPU". The
  runtime packages are already declared centrally (`Whisper.net`,
  `Whisper.net.Runtime`, `Whisper.net.Runtime.Cuda`, all 1.9.1); referencing
  them needs no version attribute.
- Word timestamps are expected to arrive as **token**-level timestamps on each
  segment (enabled on `WhisperProcessorBuilder`; confirm the exact member
  against the installed 1.9.1 package before building on it) rather than from a
  ready-made word API. Whisper.cpp emits sub-word tokens, with a word's first
  token carrying the leading space, so **tokens must be joined into words
  before they become `Word` records** — one `Word` per whitespace-delimited
  word, taking the first token's start and the last token's end. Getting this
  wrong shows up as plausible-looking but systematically early word boundaries.
  `Word.Text` is matched against the Bad Words List downstream, so lowercase
  and strip punctuation the way `Legacy/src/aligner.py` did
  (`w["word"].lower().strip()`).
- The tolerance question ("is DTW precise enough?") is still open — see below.
  If the answer is no, `PassThroughAligner` is the seam a real aligner replaces
  and no pipeline code changes.

## Contracts (frozen at T02)

In `FoulFilterNet.Domain`:

- Artifacts: `Segment`, `Word`, `Candidate`, `Hit`, `Transcript`,
  `TranscriptionResult`, `MediaInfo`
- Enums: `CensorMethod`, `MediaKind`, `SmartCutOutcome`, `SmartCutMode`
- `Times` — rounding plus the two filtergraph formatters. **Use these, never
  `ToString()`**; `Times.ToRepr(1.0)` is `"1.0"` where C# would give `"1"`.
- Jobs: `JobProgress`, `JobRequest`, `JobSummary`, `JobCancelledException`
- Smart Cut: `SmartCutDecision` (three-valued: KeepOriginal / Reject / Adjust),
  `SmartCutOptions`

In `FoulFilterNet.Domain.Abstractions`: `ITranscriber`, `IAligner`,
`ISmartCutAdvisor`, `IMediaProber`, `IAudioPreparer`, `IMediaEditor`,
`ITranscriptStore`, `IMediaPipeline`.

If one of these is wrong, **report it — do not change it**. Six streams write
test doubles against these.

## Stream A output (T03–T06) — what the rest of the port can use

New public types in `FoulFilterNet.Domain`, all pure and static-or-trivial to
construct. No frozen contract was touched.

- `Tokenizer.Tokenize(text)` — the `[a-z0-9']+` token list over lowercased text.
  `Tokenizer.Normalize(text)` is those tokens space-joined: the one spelling
  everything else compares against.
- `BadWordsList.FromLines(lines)` — normalization plus the empty-list check.
  `Contains(phrase)` expects an already-normalized phrase.
- `PhraseMatcher.FindCandidates(segments, badWords)` and
  `PhraseMatcher.FindHits(words, badWords)`.
- `HitMerger` / `HitPadding` — `new HitMerger().Merge(hits)` uses the 0.15/0.25
  defaults. Padding is injectable so T21 does not have to hard-code it.
- `SmartCutMapper.Map(startIndex, endIndex, contextWindow, centerIndex, allowWidening)`
  returns a `SmartCutDecision`. **T16**: parse the response to two ints and call
  this; `SmartCutMapper.RejectIndex` is the `-1` sentinel. It throws
  `ArgumentException` on an empty window (Python raised `IndexError` there and
  the caller swallowed it), so T18 must map any throw to
  `SmartCutDecision.KeepOriginal`.

Two deliberate departures from the Python, both noted in the commits:

1. **`FindHits` normalizes each word before matching.** The Python compares the
   aligned word verbatim, relying on `aligner.py` having lowercased and stripped
   it; a word still carrying punctuation ("Hell,") silently fails to match.
   Whisper.net emits exactly that, so T13/T14 do not have to sanitize word text
   for matching to work.
2. **`BadWordsList` throws only when no line is usable** (all blank or all
   comments), exactly as `load_bad_words` does. A list whose every entry is
   longer than three tokens is *not* an error — it simply matches nothing, which
   is the Python's behaviour. Worth a second look if that ever bites.

`merge_hits` was ported faithfully, finding 2 included: T20 still owns
reconciling Candidates to Hits by time proximity. Nothing in `HitMerger` blocks
that — it takes and returns plain `Hit` lists.

## Stream D output (T15–T18) — what T21 and T29 need to know

`FoulFilterNet.SmartCut` is complete and wired for DI. No frozen contract was
touched; `ISmartCutAdvisor` was sufficient exactly as written.

- **Register with `services.AddSmartCut(configuration)`** (T29/T32). It binds the
  `SmartCut` section, registers a named `HttpClient` with a 240 s timeout, and
  resolves **one** `ISmartCutAdvisor`. T21 injects that and calls it for every
  hit; **there is no flag to check in the pipeline.**
- **Disabled, or enabled-but-unusable, resolves `NoOpSmartCutAdvisor`** —
  `IsEnabled` false, every hit answered `KeepOriginal`, never `Reject`. "Unusable"
  means Google mode with no API key or no model, or Local mode with an
  unparseable URL; each logs one warning at startup rather than failing.
  A local server that simply is not listening cannot be detected at startup, so
  that degrades per hit instead: the transport reports it unavailable and the hit
  keeps its original timestamps.
- **`GET /config` should report `ISmartCutAdvisor.IsEnabled`**, not the raw flag —
  that is what makes the UI badge honest when the key is missing (finding 1).
- **The Google API key never comes from appsettings.** `AddSmartCut` resolves a
  `GoogleApiKeySource` delegate that reads `GOOGLE_API_KEY` from configuration
  (so environment variables and user-secrets work) and falls back to the
  environment. Tests substitute the delegate; nothing logs the key.
- **`RefineAsync` never throws**, including for an empty context window, which it
  short-circuits before `SmartCutMapper.Map` can reject it — no LLM call is spent
  on one either.
- **T21 owns building the context window**: `SmartCutOptions.ContextRadius`
  (default 11) words either side, and the `centerIndex` of the target within it.
  `allowWidening` is true only for `remove` on audio (ADR-0004).
- Live-LLM scenarios ported from `Legacy/src/test_ai_filter.py` live in
  `LiveLlmSmartCutTests` and are skipped unless `RUN_LIVE_LLM_TESTS` is set, the
  same gate the Python used. CI has no LLM and no network; every other transport
  test drives a fake `HttpMessageHandler`.

## Open questions

- Whisper.net DTW boundary error vs the wav2vec2 forced aligner it replaces.
  Measured in T13 against the manifest; if it exceeds the 0.15/0.25 s padding,
  T14's seam gets a real implementation in place of `PassThroughAligner`.
  **Not yet answered.**
