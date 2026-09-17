# STATUS — Port Progress

**Single source of truth for where the port is.** Every agent updates this file
as part of the task it completes, in the same commit as the work. If you are
resuming this project cold, read this file first and trust it over memory.

Last updated: 2026-09-16

---

## Current state

| | |
|---|---|
| Phase | **Wave 1** — streams unblocked |
| Branch | `main` |
| Solution | `FoulFilterNet.slnx`, 8 production + 8 test projects, builds clean |
| Tests | 1031 passing, 13 skipped (4 opt-in live-LLM + 9 opt-in GPU) |

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

## Execution policy: one subagent at a time

**Each remaining task goes to its own subagent, and only one runs at a time.**

The two constraints behind this pull in different directions and this is what
satisfies both. Concurrent agents exhausted the session token budget twice
(2026-09-16), so fanning out is off. But driving every task from the main
session burns its context on build output and test logs, which is both
expensive and self-limiting. A subagent per task keeps that noise out of the
integrator's context while still only ever running one at a time.

The integrator's job is therefore: brief one agent, verify what it returns,
merge, update this ledger, brief the next.

Consequences for whoever picks this up:

- One task, one agent, one commit, one merge - then the next.
- Keep committing per task. That discipline is what made the interruptions cost
  one task instead of seven.
- The dependency graph in
  [03-parallelisation-review.md](03-parallelisation-review.md) still decides
  *order*; it no longer decides *batching*.

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
| T13 | Whisper.net transcriber (CUDA) | ✅ | `task/T13-whisper-transcriber` | `47230c2` | `WhisperTranscriber`, `WhisperNetEngine`, `WhisperWords`; DTW boundaries measured (ADR-0006); `PendingTranscriber` deleted; see output below |
| T14 | Aligner seam | ✅ | `task/T14-aligner-seam` | `ffe8d32` | `PassThroughAligner`; branched from T12 |
| T15 | Smart Cut prompt | ✅ | `task/T15-smartcut-prompt` | `b9ef3fa` | `SmartCutPrompt`; template ported verbatim |
| T16 | Smart Cut response parsing | ✅ | `task/T16-response-parsing` | `779024c` | `SmartCutResponseParser`, `SmartCutResponses` |
| T17 | LLM transports | ✅ | `task/T17-llm-transports` | `2f28211` | `ISmartCutTransport`, `GeminiTransport`, `OpenAiCompatibleTransport` |
| T18 | Smart Cut advisor (flag, default off) | ✅ | `task/T18-smartcut-advisor` | `f052d4f` | `LlmSmartCutAdvisor`, `NoOpSmartCutAdvisor`, `AddSmartCut`; see notes below |
| T19 | Transcript store | ✅ | `task/T19-transcript-store` | `050a56e` | `TranscriptStore`; a version mismatch is a miss (finding 4) |
| T20 | Candidate/Hit reconciliation | ✅ | `task/T20-hit-reconciliation` | `ba9a797` | `HitReconciler`; by time proximity, fixes finding 2; branched from T19 |
| T21 | Pipeline orchestrator | ✅ | `task/T21-pipeline-orchestrator` | `71e4ce5` | `MediaPipeline`; real `IMediaPipeline` registered, closes finding 1; see notes below |
| T22 | Model release policy | ✅ | `task/T22-model-release` | `ac0e442` | `ReleasePolicyTranscriber`; the pipeline releases in a `finally`, the flag is honoured by the decorator; see notes below |
| T23 | Job queue + worker | ✅ | `task/T23-job-queue` | `85e4dbb` | `JobManager`, `JobWorker`; channel + per-job CTS |
| T24 | Job event fan-out | ✅ | `task/T24-job-events` | `06261d5` | Slow subscriber cannot stall the worker |
| T25 | Upload handling | ✅ | `task/T25-uploads` | `753bd51` | `UploadFileName`, `UploadStorage`, `UploadRequestReader` |
| T26 | HTTP endpoints | ✅ | `task/T26-endpoints` | `2f6e641` | `PendingMediaPipeline` is gone; T21 registers the real pipeline |
| T27 | Server-sent events | ✅ | `task/T27-sse` | `b44b777` | `EventEndpoints`; snapshot on connect, unnamed events, ends on shutdown |
| T28 | Zip download | ✅ | `task/T28-zip-download` | `6e6985c` | Streamed, not temp-filed; entry names fix finding 3 |
| T29 | Front end + Smart Cut wiring | ✅ | `task/T29-front-end` | `74044d5` | UI ported unchanged; advisor wiring closed by T21 |
| T30 | Startup housekeeping | ✅ | `task/T30-startup-housekeeping` | `9d3388c` | `StorageHousekeeping`; first hosted service, wipes uploads+scratch only |
| T31 | CLI | ✅ | `task/T31-cli` | | `FoulFilterCommandLine`, `CensorMethodResolution`, `JobRunner`; salvaged after an interruption; see output below |
| T32 | Container + configuration | — | | | |
| T33 | Documentation + ASR ADR | — | | | |
| T34 | Evaluation harness | — | | | Stretch |

## Completed outside the ledger

| What | Commit | Notes |
|---|---|---|
| Docs: plan, analysis, breakdown, parallelisation review | `cdcd2f8`…`3fc3e06` | |
| Media fixtures + generator | `6bdeda6` | 7 fixtures, exact ground truth in `manifest.json` |
| `appsettings.json` with the feature flags | `eaf26ee` | The docs and `.gitignore` both described a file that did not exist; `Storage` deliberately excluded, see the flag block |
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

## Interrupted work, second occurrence (2026-09-16)

A second usage limit killed Stream E mid-T27 and T13 before it wrote anything.
Per-task committing worked exactly as intended: **T23-T26 were committed and
are merged**; only the in-progress task was lost. Discarded uncommitted work,
safe to start clean:

- **T13** - nothing but a csproj edit.

This is the second interruption, and the reason the execution policy above is
now sequential.

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
- **`TranscriptionOptions.UnloadAfterJob`** is the flag, and T22 has already
  wired it: the pipeline calls `ReleaseAsync` after every job and
  `ReleasePolicyTranscriber` decides whether that reaches the engine. T13 writes
  the release itself and nothing else - see the T22 output section below for the
  three properties it has to have.
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
- The tolerance question ("is DTW precise enough?") is **answered** — yes, with
  the DTW pass rather than the token-timestamp heuristic. `PassThroughAligner`
  stays as the seam. See ADR-0006 and the T13 output section.

## T31 output — what T32 and T33 need to know

`foulfilter` (`src/FoulFilterNet.Cli`) is `find_and_remove.py` argument for
argument, composing the same engines Web does. Verified end to end on this host:
`single_hit.mp3` with `large-v3-turbo` found `damn` at 3.33-4.19 s padded, on
the **Cuda** runtime, in 16 s wall clock including the cold model load.

- **`System.CommandLine` 2.0.12 is the post-GA API.** `RootCommand.Add` for
  arguments and options, options configured through object initialisers
  (`Description`, `HelpName`), `AcceptOnlyFromAmong`, `Command.SetAction((parseResult,
  ct) => ...)`, `Command.Parse(args).InvokeAsync(new InvocationConfiguration { ... })`,
  and `ParseResult.GetValue` / `GetRequiredValue`. `SetHandler` and the beta
  shapes do not exist.
- **Parsing and mapping never run anything.** `FoulFilterCommandLine.ToRequest`
  turns a `ParseResult` plus an environment lookup into a `JobRequest`, which is
  how the precedence matrix is tested as a unit. `--no_edit` is `Render = false`.
- **Precedence is the Python's whole chain**: `--censor_method`, then `--bleep`,
  then `--delete`, then `CENSOR_METHOD`, then silence. An unrecognised variable
  falls back to silence rather than failing the run.
- **One deliberate departure**: `--censor_method delete` is accepted. The
  Python's `choices` rejected it while its environment variable accepted it, so
  the flag and the variable disagreed about a word in every legacy `.env`.
- **The CLI never downloads a model.** It wires `WhisperModelSource` without an
  acquisition delegate, so missing weights fail with the path it wanted. Web
  downloads on first use; a terminal run should not pull 1.6 GB silently.
- **Configuration**: `Host.CreateApplicationBuilder` reads `appsettings.json`
  from the working directory and the environment, so `Transcription__Model`,
  `Transcription__ModelDirectory`, `CENSOR_METHOD` and `TRANSCRIPT_DIR` all work.
  The CLI ships no `appsettings.json` of its own — T32 should decide whether it
  shares Web's. `TRANSCRIPT_DIR` defaults to the Python's `/data/transcripts`,
  which on native Windows means the root of the current drive; T32 owns that
  default too.
- **Failures are one line and an exit code**: 1 for an unreadable or
  unrecognised file or a failed job, 130 for a cancelled one. The probe sits
  inside the handled region, so a missing file is not a stack trace.
- **The scratch directory is left behind** (`.<name>_scratch` beside the
  output), exactly as the Python left it: `--debug` writes `transcript.txt`
  there, and that is where a user looks for it. Web's scratch is wiped at startup
  by T30; nothing wipes the CLI's.

## T13 output — what T31, T32, T33 and T34 need to know

Transcription is real, and `PendingTranscriber` is gone. `WhisperTranscriber`
owns everything around inference (conversion, Rescan padding, rebasing,
temporary-file cleanup) and `WhisperNetEngine` owns inference itself (model
lifetime, device selection, token joining). `Program.cs` puts the engine inside
`ReleasePolicyTranscriber`, exactly where the placeholder used to sit, so the
`UNLOAD_MODELS_AFTER_JOB` flag still decides whether a release reaches the model.

- **The Whisper.net 1.9.1 API, verified against the installed package** rather
  than against any documentation:
  - Word timestamps come from `WhisperProcessorBuilder.WithTokenTimestamps()`
    (no argument). There is **no word-level API**: `SegmentData.Tokens` is a
    `WhisperToken[]`, and `WhisperToken` is a class of public **fields** whose
    `Start`, `End` and `DtwTimestamp` are raw whisper.cpp **centiseconds** —
    unlike `SegmentData.Start`/`End`, which are `TimeSpan`. Reading the token
    fields as milliseconds would place every word a hundred times too early.
  - **DTW word timestamps are a factory option, not a processor one.**
    `WhisperFactoryOptions` is a struct (start from `.Default`) carrying
    `UseGpu`, `UseDtwTimeStamps` and `HeadsPreset`, and DTW produces nothing
    without a `WhisperAlignmentHeadsPreset` that matches the model — hence
    `WhisperModelFiles.AlignmentHeadsFor`.
  - **Device selection is an order of native runtimes**, process-global and read
    only while the first factory loads: `RuntimeOptions.RuntimeLibraryOrder`, a
    `List<RuntimeLibrary>` on a static class, over `Cpu, Cuda, Cuda12, Vulkan,
    CoreML, OpenVino, CpuNoAvx`. "CUDA if usable, else CPU" *is* that order, and
    forcing CUDA is the same order with no CPU entry left — so an unusable card
    fails to load a model instead of quietly transcribing at CPU speed.
    `RuntimeOptions.LoadedLibrary` afterwards says which one won, and it is
    logged at Information.
  - `WhisperFactory` is `IDisposable` and holds the model; a `WhisperProcessor`
    is built per transcription. `ProcessAsync(Stream, ct)` yields
    `IAsyncEnumerable<SegmentData>` and wants 16 kHz mono PCM WAV.
- **Token joining needed two things these notes did not predict.** First token's
  start to last token's end is the right shape, but on `t0`/`t1` it is not
  accurate enough — 0.31 s mean and 0.90 s worst-case error, which escapes the
  hit padding and would have left profanity audible. The DTW instants are about
  three times better *and* mark where a token **ended**, so a word starts at the
  instant of the token *before* it (reading them as starts puts every word ~0.3 s
  late). Second, whisper.cpp reports `t0 == t1` for whole words often enough to
  matter — 9 of the fixtures' words, including "Long" and "horse" — and
  `aligner.py`'s rule of dropping those costs **detection**, so they are kept
  with a minimal span instead and the padding covers the difference.
- **`IAudioPreparer.PadStartAsync(path, 0)` is the conversion path.** The frozen
  contract has no "render this as an analysis WAV" member, and padding by zero is
  exactly that (`adelay=0|0` plus the mono 16 kHz render), which makes the first
  pass and the Rescan Pass one code path with a different offset. Nothing was
  changed for T13; if that contract is ever revisited, the honest signature is
  `ToAnalysisWavAsync(path, offsetSeconds)`.
- **Weights.** `Transcription:ModelDirectory` (default `models`) says where GGML
  weights live, and `WhisperModelSource` resolves `ggml-<size>.bin` there. It
  downloads only when it is constructed **with** an acquisition delegate, into a
  `.downloading` temporary that is then moved into place, so an interrupted fetch
  cannot leave a truncated model that loads and mis-transcribes. Web wires the
  real downloader, so **a first job on a fresh machine fetches the model** (1.6 GB
  for `large-v3-turbo`). Weights are gitignored and must never be committed.
- **Still open: CUDA has not run here.** This host has an RTX 3080 Ti and a CUDA
  13.2 driver, but no CUDA toolkit runtime (no `cudart`/`cublas`), so the loader
  fell through its order to `Cpu` — about 28 s per 8 s fixture, and every number
  in ADR-0006 was measured there. Closing it needs the CUDA 13 runtime installed
  on the host and, for a machine on 12.x drivers, **`Whisper.net.Runtime.Cuda12`
  declared in `Directory.Packages.props`**, which is not there today — T13 did
  not add it, per the shared-files rule. The CUDA natives themselves do reach the
  Web output transitively, so no csproj needs changing for them.
- **Opt-in GPU tests.** `RUN_GPU_TESTS=1 dotnet test FoulFilterNet.slnx` with
  weights in `models/`; `FOULFILTER_MODEL_DIR` and `FOULFILTER_TEST_MODEL`
  override where and which. Nine facts, all passing against `large-v3-turbo`, and
  they are wired with **no** download delegate so they can never fetch anything.
  CI is unaffected — skipped by default, like the live-LLM four.

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

## T29's front end (and the wiring T21 has now closed)

The front end ported across untouched, as predicted: `index.html`, `app.css` and
`app.js` are byte-identical to `Legacy/src/static`, served at `/` with the assets
mounted at `/static`. **No API change was needed** - every field and spelling the
UI reads (`job_ids`, `detail`, `filename`, `ai_enhance`, `max_upload_mb`, the
unnamed SSE events) is already what the endpoints produce.

Both debts T29 left with the orchestrator are **closed by T21** (`71e4ce5`):
`Program.cs` calls `services.AddSmartCut(configuration)`, and `GET /config`
reports `ISmartCutAdvisor.IsEnabled` rather than the raw flag - so turning the
flag on without a key no longer produces a dishonest badge. That was the last
live half of finding 1.

## T22 output — what T13, T14 and T32 need to know

`UNLOAD_MODELS_AFTER_JOB` is honoured after every job. The policy is split the
way Stream D split the Smart Cut flag, and for the same reason: **there is no
flag to check in the pipeline.**

- **`MediaPipeline.RunAsync` releases unconditionally, in a `finally`**, so a
  job that threw or was cancelled hands its VRAM back exactly as a finished one
  does - including a job cancelled before it loaded anything. The release runs
  *after* the render, because the engines have to stay usable while the file is
  still being written.
- **`ReleasePolicyTranscriber` (in `FoulFilterNet.Transcription`) owns the
  flag.** It wraps `ITranscriber`, delegates `ReleaseAsync` only when
  `TranscriptionOptions.UnloadAfterJob` is true, and passes both transcribe
  calls straight through. `Program.cs` registers it around the transcriber, so
  **T13 replaces `PendingTranscriber` inside that registration and leaves the
  wrapper where it is** - registering a bare `ITranscriber` would silently drop
  the flag.
- **Three properties T13's `ReleaseAsync` must have**: idempotent (it is called
  after every job, and the tests call it twice), safe when no model was ever
  loaded (a job that resumed a cached transcript never transcribed, and must not
  fail on the way out), and cheap enough to sit on the critical path of every
  job, because that is where it now sits.
- **A failed release never changes a job's outcome.** Each engine is released
  independently and any throw is logged at Warning and swallowed, exactly as
  `_release_models_if_configured` did: a wedged driver must not turn a finished
  job into a failed one, nor replace the failure a failed job needs to report.
- **The aligner is released too**, because the Python released both models and
  `IAligner` carries the same `ReleaseAsync`. `PassThroughAligner` holds nothing,
  so this is free today - but **if T14 ever puts a real model behind that seam it
  needs its own flag-honouring wrapper**, or it will unload regardless of the
  flag. That wrapper is the one piece of this policy deliberately left unwritten:
  there is no implementation yet that would exercise it.
- **The harness carries the seam for this**: `PipelineHarness.EngineCalls` is the
  ordered list of transcribe / render / release calls a test asserts against, and
  `ReleaseThrows` makes releasing fail the way a wedged driver would.

## T21 output — what T22, T13 and T31 need to know

`MediaPipeline` in `FoulFilterNet.Pipeline` is the whole spine: probe, extract
audio, resume-or-transcribe, optional rescan, match, align, reconcile, merge,
refine, render. It reports the Python's exact stage names and percentages and
re-checks cancellation at every checkpoint, not merely between stages.

- **It takes a `TranscriptStoreFactory` delegate**, not an `ITranscriptStore`,
  because the store is scoped to the request's transcript directory and the
  pipeline is a singleton. Tests assert which directories it asked for.
- **Alignment is all-or-nothing (ADR-0001)**; a candidate the aligner never
  placed is logged and dropped rather than censored at a guessed timestamp.
  Widening is allowed only for `remove` on audio (ADR-0004).
- **It releases the engines in a `finally`** - closed by T22, which also covers
  the failure and cancellation paths rather than only the happy one.
- **Transcription is the only engine still stubbed.** `PendingTranscriber` in Web
  throws `NotSupportedException` with a message the UI shows; its `ReleaseAsync`
  is a deliberate no-op because it holds no VRAM. T13 replaces the registration
  in `Program.cs` and nothing else. A file whose transcript is already cached
  runs end to end today (ADR-0002), which is how the resume path is exercised.
- **Logging is `[LoggerMessage]`-generated** throughout; the Pipeline project
  takes `Microsoft.Extensions.Logging.Abstractions` only.

### Why this one was salvaged rather than discarded

The third usage limit killed T21 mid-verification, and the standing policy is to
discard ambiguous uncommitted work. This was the documented exception: the tests
were demonstrably written first (the agent recorded them red against
`NotImplementedException`), the implementation was complete rather than a
skeleton, and the build failed on **analyzer rules only** - five CA1873 and one
CA1068, both mechanical. Fixing those took the suite from 721 to 827 passing
with nothing else changed, which is the evidence that the salvage was sound.

## T19 output — what T21 needs to know

`TranscriptStore` in `FoulFilterNet.Pipeline` implements the frozen
`ITranscriptStore` against a directory handed to its constructor
(`new TranscriptStore(request.TranscriptDirectory)`).

- **`SaveAsync` keys the file off `transcript.FileHash`, not off a separate
  argument.** T21 must build the `Transcript` with the digest that
  `ComputeHashAsync` returned; the `baseName` argument only supplies the
  human-readable half of the file name and is truncated to 80 characters and
  scrubbed to `[A-Za-z0-9-_.]` exactly as the Python did.
- **Nothing a cache entry can contain will fail a job.** Unreadable, unparseable,
  wrong schema version, or an inner `file_hash` that disagrees with the lookup
  key are all misses; the worst case is a re-transcription. Cancellation is the
  one thing that propagates, so a cancelled job stops instead of falling through
  to the GPU.
- **One digest keeps one file.** Saving again (T21 saves after alignment)
  replaces the earlier entry rather than leaving a word-less transcript beside
  the refined one. Writes go to a `.writing` temporary and are then moved into
  place, so an interrupted save cannot poison the cache.
- The store takes no logger - the Pipeline project has no logging dependency.
  The Python logged "Reusing persisted transcript" at the call site, and that is
  where it still belongs, in T21.

## T20 output — what T21 needs to know

`HitReconciler` in `FoulFilterNet.Pipeline` replaces the hit-confirmation block
in `pipeline._run_job_impl`. One call does the whole stage:

```csharp
var hits = new HitReconciler().Reconcile(candidates, words, badWords);
hits = new HitMerger().Merge(hits);
```

- **It calls `PhraseMatcher.FindHits` itself**, so T21 hands it the aligned words
  rather than pre-computed hits. It pads and merges nothing - that stays
  `HitMerger`'s job, run afterwards, exactly as the Python ordered it.
- **The proximity tolerance is 0.40 s**, the pre-padding plus the post-padding,
  which is the gap at which `HitMerger` fuses two windows. Inside that distance
  the two possible decisions produce the same final cut, so the tolerance sits
  exactly where the choice starts to matter. A reconciler built with a custom
  `HitPadding` derives its tolerance from that padding - if T21 ever passes
  custom padding to the merger, pass the same padding here.
- **A fallback hit has a null `WordIndex`**; an aligned one carries the index of
  the word it came from. Smart Cut's context window is built from word indices,
  so for a fallback there is no exact index to trust - the Python's
  `_context_window` picked the word whose start was nearest the hit, and that
  approach still works for these.
- Coverage requires the same phrase *and* proximity. Position alone would let one
  word's aligned Hit suppress a different word's Candidate at the same moment,
  which is the same class of bug as finding 2 in the other direction.

## Open questions

- ~~Whisper.net DTW boundary error vs the wav2vec2 forced aligner it replaces.~~
  **Answered in T13 — see [ADR-0006](adr/0006-whisper-net-collapses-transcription-and-alignment.md).**
  Measured against the manifest: 0.107 s mean / 0.242 s worst start error and
  0.069 s / 0.207 s at the end, with every one of the 11 planted hits inside the
  0.15/0.25 s padding (worst late start 0.070 s, worst early end 0.207 s). So
  `PassThroughAligner` stays and T14's seam needs no real implementation. The
  answer holds **only** for the DTW pass with its instants read as token ends:
  whisper.cpp's older `t0`/`t1` heuristic errs by 0.90 s at worst, which the
  padding does not absorb.
- **CUDA has never actually run on this host.** Every measurement above was
  taken on the CPU, because the machine has no CUDA toolkit runtime installed -
  see the T13 output section. Timestamps are backend-independent, so the numbers
  stand; throughput is what is unverified.
