# STATUS — Port Progress

**Single source of truth for where the port is.** Every agent updates this file
as part of the task it completes, in the same commit as the work. If you are
resuming this project cold, read this file first and trust it over memory.

Last updated: 2026-09-19

---

## Current state

| | |
|---|---|
| Phase | **All 34 tasks merged** — runs natively; the container was dropped. Web video (W00–W18) in progress, see below |
| Branch | `main` |
| Solution | `FoulFilterNet.slnx`, 11 production + 11 test projects, builds clean (plus the `extension/` Node tests) |
| Tests | 2961 passing, 38 skipped (4 opt-in live-LLM + 23 opt-in GPU + 11 opt-in web); extension: 1958 passing |

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
| T31 | CLI | ✅ | `task/T31-cli` | `4203dc7` | `FoulFilterCommandLine`, `CensorMethodResolution`, `JobRunner`; salvaged after an interruption; see output below |
| T32 | Container + configuration | ✅ | `task/T32-container-config` | `7f04e44` | `LegacyEnvironmentVariables`, `DataLocations`, `ConfigurationKeys`; native run verified; container files later dropped; see output below |
| T33 | Documentation + ASR ADR | ✅ | `task/T33-documentation` | `59aabe2` | Root `README.md` and `CONTEXT.md`, docs index; the ADR was T13's (ADR-0006); doc/code discrepancies fixed in docs, see output below |
| T34 | Evaluation harness | ✅ | `task/T34-evaluation-harness` | `1b12965` | `foulfilter-eval` (`FoulFilterNet.Evaluation`): `BoundaryScorer`, `ScoreCard`, `FixtureEvaluator`; reproduces ADR-0006 exactly on the CPU, one word 20 ms apart on CUDA; see output below |

## Web video tasks (docs/04-web-video-plan.md)

Same status key and conventions as the port ledger; branches are `task/W<nn>-<slug>`.

| Task | Description | Status | Branch | Commit | Notes |
|---|---|---|---|---|---|
| W00 | Land windowed transcription | ✅ | `task/W00-windowed-transcription` | `ae10529` | `TranscriptionWindows` (28 s windows, 6 s overlap, each word/segment kept by the one window whose share holds its midpoint); `WhisperNetEngine` reads the WAV per window because DTW silently stopped at 30 s. Edge-case `Plan`/`Stitch` tests (empty, exact multiple, just over one window, share-boundary word, every instant owned once) added in the W00 ledger commit. `RUN_GPU_TESTS=1`: all 10 `LiveWhisperTranscriberTests` pass on an RTX 3080 Ti, including `HearsTheProfanityPastTheFirstThirtySeconds` |
| W01 | Spike: prove the two load-bearing assumptions *(throwaway)* | ✅ | `task/W01-spike` | `efc395a`, `eaa000c` | ADR-0007 accepted. Web Audio on YouTube works (±5 ms); ~0.7 s/window; first play 11–24 s as planned → W17 required, no `--download-sections`. Autoplay, background tab and ads untested → W16 in stock Chrome |
| W02 | ADR-0007, vocabulary and scaffolding | ✅ | `task/W02-scaffolding` | `ab27f20` | ADR-0007 was already written and accepted in W01. Vocabulary moved to CONTEXT.md (`### Web video`). `FoulFilterNet.Sources` (Domain, Media) and `FoulFilterNet.Watch` (Domain, Pipeline, Transcription, Sources) + test projects, each with a skipped `Scaffolding.cs` placeholder as in T01 - delete it with the first real test. Web does not reference them yet (W10). `extension/`: MV3 manifest with no scripts, `npm test` = `node --test "test/**/*.test.js"` with no dependencies; one smoke test. A bare `node --test test/` fails on Node 22.16 on Windows (treated as a module path), hence the glob. CI runs it on Node 22 |
| W03 | VideoRef | ✅ | `task/W03-videoref` | `698b64f` | `VideoRef` (sealed record, private ctor, get-only props so `with` cannot bypass validation): `TryCreate(provider, id, out)` for network input (never throws), `Create` for trusted code (`ArgumentException` naming `provider`/`id`), `Key` = `youtube-<id>`, `WatchUrl` = `https://www.youtube.com/watch?v=<id>`, `TryParseKey`/`ParseKey`, `ToString()` = `Key`, consts `YouTubeProvider`, `YouTubeIdLength`. Provider matched `OrdinalIgnoreCase` and stored lower case, but **not trimmed**; ID is exactly 11 of ASCII `[A-Za-z0-9_-]`, case-sensitive. Key parsing is strict: canonical lower-case provider only, split at the first dash (`youtube--abcdefghij` is id `-abcdefghij`), so every parsed key round-trips byte-for-byte. **W08: an ID can start with `-`; pass `WatchUrl`, never `Id`, to yt-dlp.** 170 tests; Sources.Tests placeholder deleted |
| W04 | Coverage | ✅ | `task/W04-coverage` | `92c55de` | `Coverage` (sealed, immutable) + `CoverageInterval(From, To)` record struct (closed, `Length`). `Coverage.From(plan, duration, finished, guard = DefaultGuardSeconds)` and `Coverage.ForDuration(duration, finished, guard)` (plans with `TranscriptionWindows.Plan`); `Intervals`, `DurationSeconds`, `IsComplete` (every window finished → exactly `[0, d]`, even for d = 0), `IsEmpty`, static `Empty`, `Contains(from, to)` (inside one closed interval), `CoveredAheadOf(position)`. Runs merge by window **index**, not time, so no float tolerance; `From` checks the plan tiles exactly (first KeepFrom ≤ 0, last KeepTo ≥ d, `KeepFrom[i] == KeepTo[i-1]`) and throws `ArgumentException` otherwise. Guard applied at an edge next to an unfinished window unless clamping put that edge at 0 or d; runs left with no length (≤ 2 guards) are dropped. Throws `ArgumentOutOfRangeException` for indices outside the plan and negative/NaN/infinite duration or guard; `ArgumentException` for NaN positions or `to < from`. Duplicate indices ignored. Intervals are a linear scan (≈ 150 windows for an hour). 125 tests; Watch.Tests placeholder deleted |
| W05 | Window scheduler | ✅ | `task/W05-window-scheduler` | `d22c825` | Static `WindowScheduler.Next(plan, finished, playheadSeconds)` → `int?` (null when all done) and `Order(...)` → `IReadOnlyList<int>` (the whole remaining order; both come from one private iterator, so `Next == Order[0]`). Rule: start at the window whose **share** holds the playhead (`KeepFrom <= p < KeepTo`, so a boundary goes to the later window, an overlap by share not audio range), take unfinished windows upwards to the end, then wrap **backwards from the playhead, nearest first** (not lowest index: short rewinds are the likeliest backward seek, and this grows the run the playhead is in; a jump to 0 moves the playhead anyway, so costs one window). Deviation from the plan text ("share starts at or after the playhead"): the holding window comes first. Playhead clamped to the plan (negative/-inf → first, past the end/+inf → last); **NaN throws** `ArgumentException` (`playheadSeconds`), like Coverage. Plan checked: non-empty, each share has length and starts exactly where the previous ends (`ArgumentException`); finished indices outside the plan → `ArgumentOutOfRangeException` (`finished`), repeats ignored, set read once. No in-flight notion: **W09 must add the in-flight window to `finished` (or skip it) before calling `Next`**, and call it after each window with the latest playhead. 113 tests incl. a 1000-state seeded property check |
| W06 | Partial Hit snapshot | ✅ | `task/W06-partial-hit-snapshot` | `433f35b` | `WatchProgress` (sealed, immutable accumulator): `Start(duration, badWords)` / `Start(plan, duration, badWords)` (plan checked via `Coverage.From`), `With(index, TranscriptionResult heard)` (heard on the **window-local** timeline, as the engine returns it), `WithBadWords(list)`, `IsFinished(i)`, `Plan`, `DurationSeconds`, `BadWords`, `Revision` (long), `FinishedWindows` (ascending), `Coverage`, and `Snapshot` (lazy, built once per instance). `HitSnapshot` record: `Revision`, `Coverage`, `Hits`, `Transcript` (`TranscriptionResult`, stitched, file timeline), `FinishedWindowCount`, `TotalWindows`, `IsComplete`. **Revision is owned by the accumulator**: 0 at start, +1 per `With`, +1 per `WithBadWords` that changes the phrases (same phrases → same instance, no bump), so the Bad Words List is modelled and a reread list only counts if it changed. **A second result for a finished window throws `InvalidOperationException`** (finished shares are final; the viewer may have heard them). Rules reused, not copied: `PhraseMatcher.FindCandidates` → `HitReconciler` → `HitMerger`, both on `HitPadding.Default`; **no Pipeline change was needed** (MediaPipeline's private `Reconcile` only adds logging). **Deviation/finding**: the rules run **per run of consecutive finished windows** and the runs' Hits are merged together - stitching windows 0 and 2 without 1 would let a phrase match across the unheard gap. `WordIndex` is shifted to index the whole snapshot transcript. Complete → one run → exactly the batch result; any completion order gives the same snapshot. Cross-share phrase falls out naturally for Words; but a *Segment* kept by window A that contains a word of window B's share gives a segment-estimate (fallback) Hit as soon as A is done, replaced by the Words' Hit once B is done (over-censors briefly; tested). **All** Hits are sent, not only those inside Coverage (the gate guards uncovered audio). No aligner, Smart Cut or Rescan. **W09**: keep one `WatchProgress` per session, swap in `With(...)` after each window, serve `Snapshot`; `FinishedWindows` feeds `WindowScheduler.Next`; `Snapshot.Transcript` is the Transcript to save when `IsComplete`. 108 tests |
| W07 | Windowed engine contract and priority lane | ✅ | `task/W07-windowed-engine-lane` | `019778c` | `IWhisperEngine` is now `OpenAsync(wavPath, InferencePriority priority = Normal, ct)` → `IAnalysisAudio` (`DurationSeconds`, `Windows`, `TranscribeWindowAsync(index, ct)` → result on the **window-local** timeline; `IAsyncDisposable`) plus `ReleaseAsync`. **Priority is chosen at open, not per call** (it belongs to who is listening): W09 opens at `High`, batch at `Normal`. `TranscribeWavAsync` survives as the extension `WhisperEngineExtensions.TranscribeWavAsync` (open at Normal, windows 0..n-1, `Stitch`, audio closed before return/throw so the WAV can be deleted); `WhisperTranscriber` calls it, no other caller or composition root changed. New public pieces: `InferencePriority`, `InferenceLane` (`EnterAsync(priority, ct)` → `Lease : IDisposable`; High waiters before Normal, FIFO within; a waiter cancelled while queued leaves at once with OCE and is never admitted; a holder's lease stays good until disposed; double dispose is a no-op; `IsHeld`, `Waiting`), `AnalysisWav` (the old inline WAV code moved out unchanged: `WaveParser` header, frames = header data size / frame bytes, rounding, channel averaging, short reads; positionless `RandomAccess` reads so windows read concurrently; file held open `FileShare.Read` until disposed), `IWindowListener` + `WindowedAudio` (lane per window at the audio's priority, samples read outside the lane, one listener built lazily on the first window and reused, disposal waits for a window in flight). `WhisperNetEngine` owns one lane (it is a singleton everywhere, so Jobs and Watch Sessions share it); model load happens in `OpenAsync`, outside the lane. **whisper.cpp**: each Whisper.net processor has its own state and a per-processor semaphore, so concurrent `ProcessAsync` on different processors of one factory is allowed, but the lane makes inference strictly one at a time anyway; on cancellation mid-window the processor is `DisposeAsync`ed (which waits for the native abort) **before** the lane is released, and the next window builds a fresh one. **Deviation/addition**: `ReleaseAsync` with audio still open is **deferred** until the last audio closes (disposing the factory under a live processor would crash) - W09's session holds the model even if a Job with `UnloadAfterJob` ends. Each open audio that has transcribed holds one whisper state in VRAM. Log change: the per-transcription "Transcribed N segment(s)…" line became "Opened … s of analysis audio in N window(s) at P priority". Tests: 47 lane (incl. a 400-request seeded stress), 34 `AnalysisWav`, 43 `WindowedAudio`, 24 batch path, transcriber and engine tests rewritten/extended. `RUN_GPU_TESTS=1`: all 13 Transcription live tests pass (incl. `HearsTheProfanityPastTheFirstThirtySeconds` and 3 new: windows heard backwards stitch to exactly the batch words, a High watch and a Normal job run concurrently with identical words, a release while open is deferred), and all 10 Evaluation `TheRealPipelineAgainstTheFixtures` facts pass (batch output unchanged) |
| W08 | yt-dlp audio source | ✅ | `task/W08-ytdlp-audio-source` | `0f3c301` | **Contract collapsed per ADR-0007**: `IWebAudioSource.FetchAudioAsync(VideoRef, directory, ct)` → `WebAudio(Video, Title, DurationSeconds?, AudioPath, Extension, FormatId?, Codec?, JsRuntimeMissing)` in **one** yt-dlp call, plus `CheckAvailabilityAsync(ct)` → `WebVideoAvailability(YtDlpVersion?, DenoVersion?)` (`YtDlpFound`, `JsRuntimeFound`, `IsAvailable`, `Problem`) for W10's `/config`; it only runs `--version` checks (20 s timeout each → counts as missing; the caller's cancellation propagates). Impl `YtDlpAudioSource(IProcessRunner, SourcesOptions[, probeTimeout])`; `SourcesOptions` (`SectionName = "Sources"`, `YtDlpPath = "yt-dlp"`, `DenoPath` null → yt-dlp's own PATH search, else passed as `--js-runtimes deno:<path>` and probed there), not wired into Web. Args (`YtDlpArguments.ForFetch`): `--ignore-config --no-playlist [--js-runtimes deno:P] --format bestaudio --match-filters !is_live --no-progress --no-simulate --color never --paths <dir> --output audio.%(ext)s --print "pre_process:FFN-INFO %(.{id,title,duration,live_status})j" --print "after_move:FFN-FILE %(.{filepath,ext,format_id,acodec})j" -- <WatchUrl>`. Verified on the real binary: `--` ends options (`yt-dlp -- --version` → "not a valid URL"); `pre_process` prints **before** the match filter, so a skipped live stream exits 0 with an info line and no file line (→ Unsupported); `j` output is ASCII-escaped, plain `--print` gave mojibake through the Windows code page; the dir goes in `--paths` because `--output` is a template. **Failures are one `WebAudioException`** with `Kind` (`WebAudioFailure`: Failed, NotInstalled, JsRuntimeMissing, Unavailable, SignInRequired, Unsupported, Network), `Reason` (yt-dlp's ERROR line minus `ERROR: [youtube] <id>:`), `ExitCode`, `StandardError`, `Video`, `IsUnsupported`; **cancellation is a plain OCE**. Mapping is by phrase on the ERROR lines (warnings only if no ERROR line) in `YtDlpDiagnostics`, order Unavailable → Unsupported → SignInRequired → Network → Failed; a Failed run that also warned "No supported JavaScript runtime" becomes JsRuntimeMissing; a *successful* run with that warning succeeds with `JsRuntimeMissing = true`. Exit 0 is trusted only with both lines, the file directly inside the dir, and on disk. Every failure and cancellation deletes `audio.*` in the directory (retrying locks), and earlier `audio.*` is deleted before starting; other files are never touched. Fixtures in `YtDlpSamples` are real captures (success, Unicode, live-skipped, no-Deno warning, nonexistent, gone live recording, upcoming, age-restricted, connection refused, DNS failure, no format, usage error, truncated id); private/bot-check/removed/geo/members/premiere/mid-download timeout are hand-written in yt-dlp's format (none reproducible on demand). **Media change**: new `IProcessRunner`/`ProcessRunner`/`ProcessResult`/`ProcessNotStartedException` (never judges exit codes; kills the tree on cancel and waits ≤ 5 s for it to die); `FFmpegRunner` now sits on it (new ctor `(FFmpegOptions, IProcessRunner)`), messages and existing tests unchanged. `RUN_WEB_TESTS=1` (with `FOULFILTER_YTDLP`/`FOULFILTER_DENO` when not on PATH): all 11 live facts pass against YouTube (19 s "Me at the zoo": title, duration 19, one `audio.webm`, no runtime warning; `aaaaaaaaaaa` → Unavailable; the 60-min W01 video cancelled at 5 s leaves the dir empty). **W09**: give each session its own directory; `DurationSeconds` is informational, plan from the WAV; map `IsUnsupported` → Unsupported, else Failed with `Reason`. 386 new tests |
| W09 | Watch Session and manager | ✅ | `task/W09-watch-session` | `f76ade4` | `WatchSession(VideoRef, WatchSessionServices)` (`Start`, `Cancel`, `RecordHeartbeat(pos)`, `Snapshot()`, `IsExpired(now, options)`, `State`, `PlayheadSeconds`, `LastHeartbeatAt`, `EndedAt`, `Completion` - never faults). `WatchState`: Queued → Fetching → Preparing → Transcribing → Complete, or Failed / Unsupported / **Cancelled** (added: cancel and idle expiry end there; `IsTerminal()`, `IsFailure()`). **Deviation**: the plan's Resolving + Downloading are one `Fetching`, since W08 made it one yt-dlp call. Cache hit: Queued → Complete via new `WatchProgress.FromTranscript(transcript, badWords)` (one window with an unbounded share, revision 1, 1 of 1 windows, so the SAME rules and `WithBadWords` apply; duration = last Segment/Word end, since a Transcript records no length). The store matches digests ignoring case but YouTube IDs are case-sensitive, so a cached `FileHash` must equal `VideoRef.Key` ordinally or it is a miss. Run: cache lookup → `FetchAudioAsync` into `<scratch>/<key>-<guid>` → `PadStartAsync(path, 0)` → `OpenAsync(wav, High)` → `WindowScheduler.Next(plan, finished, latest playhead)` one window at a time (so nothing is in flight when the next is chosen; no window is ever redone) → save the stitched Transcript once under `youtube-<id>` (base name = title, else key; a failed save is logged, not fatal) → Complete. Audio disposed, WAV and scratch dir deleted on every outcome. `WebAudioException`: `IsUnsupported` → Unsupported, else Failed, with `Reason` (fallback `Message`, cut to 500 chars) and its `Kind` as `FailureKind`; any other exception → Failed with its message. `WatchSnapshot` (immutable record): `Video`, `Key`, `State`, `Reason`, `FailureKind`, `Title`, `DurationSeconds` (yt-dlp's while preparing, then the WAV's), `Analysis` (`HitSnapshot` or null before the plan), `Revision`, `Coverage`, `WindowsDone`/`WindowsTotal`, `Progress` (0–1, 1 when Complete), `RealtimeFactor` (share seconds / window wall time over the last 3 windows, including GPU queueing), `IsKeepingUp` (false only while Transcribing with factor < 1), `FromCache`. **Bad Words List**: `IBadWordsSource.GetCurrent()` is asked on every `Snapshot()`; a change → `WithBadWords` (new revision, no re-transcription), complete and cached sessions included; an unreadable list mid-session keeps the one in use (at start it fails the session). `FileBadWordsSource(path)` re-reads only when last-write time or length changes, and keeps the last good list over a bad edit. `WatchSessionManager` (`Heartbeat(video, pos)` → snapshot, creating/starting under one lock so racing heartbeats share one session and one fetch; `Find` (no side effects); `Cancel` (drops); `SweepExpired()`; `Count`; `Dispose`/`DisposeAsync` cancel all, async waits for cleanup). Expiry, applied by heartbeats and `Find` as well as sweeps: working with no heartbeat for `IdleTimeout` (2 min, cancels); Complete `CompletedRetention` (30 min) after the later of completion and last heartbeat; Failed/Unsupported `FailedRetention` (1 min) after ending, **not** extended by heartbeats, then the next heartbeat makes a new session (the retry, revision starts again). Position must be finite and ≥ 0 (`ArgumentOutOfRangeException` → W10's 400). `WatchOptions` (`Watch` section: the three spans, `SweepInterval` 15 s, `ScratchDirectory` = `<data>/scratch/watch`, `TranscriptDirectory`, `BadWordsPath` from `DataLocations`; `Validate()`). `WatchSessionSweeper` (`BackgroundService`, `PeriodicTimer` on the injected `TimeProvider`; note .NET 10 runs `ExecuteAsync` on the thread pool, so its timer can start after `StartAsync` returns). `AddWatch(configuration)` / `AddWatch(Action<WatchOptions>)` / `AddWatch()`: TryAdd `TimeProvider.System`, `FileBadWordsSource`, the manager (store from the host's `TranscriptStoreFactory` if registered, else `TranscriptStore`), the sweeper once. **Packages**: `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 added to `Directory.Packages.props` (tests); Watch now references Hosting.Abstractions, Logging.Abstractions and Options.ConfigurationExtensions. **W10**: register `IWebAudioSource` (`YtDlpAudioSource` + `SourcesOptions`), point `WatchOptions` paths at `StorageOptions` (same transcript dir and Bad Words file as Jobs), keep the one `IWhisperEngine` singleton, call `AddWatch`; serialise `WatchSnapshot` (enum states lower-case snake, `Analysis.Hits`/`Coverage.Intervals`; the stitched `Transcript` is probably too big to send each second). Sessions for different videos run concurrently and take turns on the lane. 411 new tests (fakes only, `FakeTimeProvider`), 6 consecutive green runs |
| W10 | Watch endpoints and host hardening | ✅ | `task/W10-watch-endpoints` | `a59192d` | **HTTP contract** (snake_case; errors are `{"detail": "..."}` like the batch API): `POST /watch` body `{"provider":"youtube","video_id":"<11>","position":<seconds>}` → 200 `WatchView` (starts the session if none, records heartbeat + playhead); `GET /watch/{provider}/{id}` → 200 view or 404, **no side effects** (does not start or heartbeat); `DELETE /watch/{provider}/{id}` → 204 / 404. Refusals: non-JSON content type (or none) → **415**; malformed JSON, `null`, non-object, wrong types → 400; provider/id not accepted by `VideoRef.TryCreate` → 400 (same on GET/DELETE routes; provider any case, id case-sensitive, **leading `-` valid**); `position` **required** (decided: a missing playhead is a client bug, not 0), finite and ≥ 0 (`1e400` reads as infinity → 400; `"NaN"` → 400; numeric strings like `"12.5"` are accepted by the Web JSON defaults). Manager disposed (shutdown) → 503. `WatchView` fields, in order: `key`, `provider`, `video_id`, `session`, `state` (queued/fetching/preparing/transcribing/complete/failed/unsupported/cancelled via `WireNames.Of(WatchState)`), `reason`, `failure_kind` (failed/not_installed/js_runtime_missing/unavailable/sign_in_required/unsupported/network, only for fetch failures), `title`, `duration`, `revision`, `unchanged`, `coverage` (`[[from,to],...]`), `hits` (`[{start,end,phrase}]`, all Hits incl. outside coverage), `windows_done`, `windows_total`, `progress` (0–1), `realtime_factor` (null until measured), `keeping_up`, `from_cache`. No transcript. Nulls are written, not omitted. **Cheap polling**: `?since=<revision>&session=<session>` on POST and GET: when **both** equal the session's current ones → `unchanged: true`, `hits: null`, every other field still current (state/progress move between revisions). Equality, not ≥; `since` without `session` is ignored (full answer); `since` must be plain digits fitting a long, else 400. **Watch change (added)**: revisions restart in a replacement session (retry after failure, cancel, idle expiry), so a bare revision is ambiguous; `WatchSession.Id` (GUID "N") and `WatchSnapshot.SessionId` (init-only, `""` for hand-built snapshots) were added. `/config` gains `web_video: {available, yt_dlp_version, deno_version, problem}`, appended after the four existing fields (unchanged). `WebVideoAvailabilityCache` (singleton + hosted service): probe started at host start, shared by concurrent callers, runs on its own shutdown token (a caller hanging up does not cancel it; host stop kills it), kept 5 min when available and 30 s when not (age from completion), a throwing probe reads as unavailable. Composition: `IProcessRunner`→`ProcessRunner`, `SourcesOptions` from `Sources`, `IWebAudioSource`→`YtDlpAudioSource`, `AddWatch(configuration)` for the timings; `WatchOptions` paths are **PostConfigured from `StorageOptions`** (scratch = `<data>/scratch/watch`, same transcript dir and Bad Words file as Jobs; a `Watch:ScratchDirectory` setting is overridden deliberately); `StorageHousekeeping` already empties `scratch/` recursively, so no change. The one `IWhisperEngine` singleton is shared. appsettings: `Sources` (`YtDlpPath`, `DenoPath`) and `Watch` (4 timings) sections. **Hardening**: `AllowedHosts` = `localhost;127.0.0.1;[::1]` (HostFiltering ignores ports when the entry has none; IPv6 needs the brackets): foreign hosts, `localhost.evil.example`, LAN IPs and `0.0.0.0` → 400 on every endpoint; `localhost[:port]`, `127.0.0.1[:port]`, `[::1][:port]` → 200 (tests; mutation with `*` makes 12 fail). **Consequence**: the service is no longer reachable by a LAN address even if bound to one. No CORS policy exists or was added (tests: no `Access-Control-Allow-Origin` on a read or a preflight). Existing UI (`/`, `/static/*`, `/config`, `/jobs`) verified on localhost:8000. Tests: stubbed yt-dlp/FFmpeg/GPU in `FoulFilterApplication` (so no Web test starts a process, except `RealPipelineApplication`, whose host start runs `yt-dlp --version`/`deno --version` via the warm-up probe; no network). `Microsoft.Extensions.TimeProvider.Testing` referenced by Web.Tests (already in Directory.Packages.props). 285 new tests (278 Web, 7 Watch); Web tests 5 consecutive green runs. **Smoke test (real, RTX 3080 Ti, large-v3-turbo, data dir in scratchpad, test list incl. "long")**: `jNQXAC9IVRw` (19 s, cold model): fetching 1 s → preparing at 4 s → first coverage and complete at 15 s (≈10 s cold model load), 2 hits ("long" ×2), realtime factor 21. `YwARwww5aFo` (649 s, warm model): preparing at 6 s, transcribing at 7 s, **first coverage `[0,24]` at 8 s**, complete (30/30 windows) at 29 s, 2 hits, factor 25. Then DELETE → 204, GET → 404, re-POST → cache hit, complete at once, `from_cache: true`, revision 1, `since`+`session` → `unchanged: true`, `hits: null`; text/plain → 415; `--exec=cal` → 400; foreign Host → 400; `[::1]` → 200. **For W11–W15**: send `Content-Type: application/json`; keep `session` and `revision` from each answer and send both back; on `unchanged` keep the previous hits; treat a changed `session` as a fresh start (drop old hits); `state` complete means fully covered - **a cached session's coverage ends at the last word heard (646.3 s of a 649.2 s video) and `title` is null**, so the gate must treat `complete` as covered to the end rather than compare coverage with `video.duration`; `failure_kind` drives the advice; a failed session is kept 1 min and re-POSTs do not retry it until then; GET does not keep a session alive, only POST does (idle timeout 2 min). |
| W11 | Extension skeleton | ✅ | `task/W11-extension-skeleton` | `e052828` | Plain ES modules, no deps. **Layout** (see extension/README.md): pure `src/settings.js` (`DEFAULT_SETTINGS` {serverUrl `http://localhost:8000`, enabled, censorMethod silence/bleep, failPolicy `closed` only, offsetMs 0}; `validateSettings`/`normaliseSettings`/`mergeSettings`, `normaliseServerUrl`, `originPattern`, `needsOptionalPermission`, `createSettingsStore({area, onChanged})` → `load` (never rejects; bad values fall back per field), `save(patch)` (writes only when valid), `subscribe`; one storage key `settings` in `chrome.storage.sync`). **Decided**: any http(s) URL accepted, trimmed, trailing slashes dropped, no credentials/query/fragment; a host other than localhost/127.0.0.1/[::1] is a *warning* (the service's AllowedHosts refuses it); offset rounded and clamped to ±500 with a warning; `remove` → silence with a warning (ADR-0004). `src/api-client.js`: `createApiClient({serverUrl, fetch, timeoutMs=3000, setTimeout, clearTimeout})` → `heartbeat`, `get`, `cancel` (404 → `{ok:true, cancelled:false}`), `config`; results `{ok:true, view|config|cancelled}` or `{ok:false, error:{kind, status?, detail?}}`, kinds unreachable/timeout/http/bad_response/invalid_request; args checked before fetching (provider `youtube`, 11-char id, finite position ≥ 0, since a safe integer); `?since=&session=` only when both set; `parseWatchView` (camelCase, strict: unknown `state` → bad_response, unknown `failure_kind` accepted, coverage ascending/non-overlapping, `hits` null only when `unchanged`) and `parseConfig` (requires `web_video`). `src/protocol.js`: messages `ff/heartbeat`, `ff/get`, `ff/cancel`, `ff/config` (optional `serverUrl` to test an unsaved URL) with constructors, `parseMessage`, `isProtocolMessage`, `isFromExtension`, `isReply`, and `createMessenger(sendMessage)` which never rejects (**added kind `extension`**: the page could not reach the worker → W15 "reload the page"). `src/relay.js`: the stateless worker logic (settings read per message). `src/connection.js`: user sentences (`describeError`, `describeConnection`). Adapters: `src/background.js` (module SW), `options.html` + `src/options.js` (form, Save, Test connection), `src/content-loader.js`, `src/content.js` (empty). **Module-loading rule for W12–W15**: the only manifest content script is the classic `src/content-loader.js`, which `import(chrome.runtime.getURL('src/content.js'))`; `web_accessible_resources` = `src/*.js` for `https://www.youtube.com/*`; keep content modules directly in `src/`; only the SW fetches. **Custom server URL**: `optional_host_permissions` `http://*/*`, `https://*/*`, requested for the one origin by Save / Test connection (first await in the click, to keep the gesture). `action` has only a title (W15 adds the badge); no icons. Scaffold `version.js` + smoke test deleted. Tests 440 (127 settings, 177 api-client, 58 protocol, 18 relay, 24 connection, 36 manifest integrity incl. import graph and WAR coverage). Not loaded in a real Chrome in this task (adapters syntax-checked only): W16's checklist covers it |
| W12 | Page watcher | ✅ | `task/W12-page-watcher` | `2b5d7a5` | Three new content-script modules, directly in `src/` (web accessible, manifest integrity test green). **`src/video-id.js`** (pure): `videoIdFromUrl(url)` → id or null, `isVideoId(id)` (the server's rule: exactly 11 of `[A-Za-z0-9_-]`, leading `-` valid; `api-client.js` now uses it instead of its own regex), `WATCH_HOST`, `WATCH_PATH`. **Decided**: https only (http → null); host exactly `www.youtube.com` after the parser lower-cases it (no port but the default, no credentials, no trailing dot); path exactly `/watch` (`/watch/`, `/watch/<id>`, shorts, embed, live, youtu.be, m./music. → null); `v` read percent-decoded; a repeated `v` counts only if every copy is identical, **conflicting copies → null** (never guess another video's Hits); fragment ignored; non-strings → null, never throws. **`src/page-state.js`** (pure): `reducePageState(state, event)` (same object back when nothing changed, else a new frozen one), `INITIAL_PAGE_STATE`, `PHASES`, `shouldMute(s)` (= `leaving`), `isFilterable(s)` (watching && !adShowing && hasVideoElement), `createPageState({onChange, initial})` → `{state, dispatch(event) → changed}`; `onChange(state, previous, event)` only on a real change. Events: `navigate-start`, `navigate-finish {url}`, `initial {url}`, `video-element {present}`, `ad {showing}`, `pagehide`; malformed ones are ignored. State `{phase, videoId, adShowing, hasVideoElement, generation}`; `videoId` is non-null exactly when `watching`. Transitions: navigate-start → `leaving` from **every** phase (idle too: the miniplayer plays on non-watch pages), `videoId` null; navigate-finish/initial with a watch URL → `watching` (generation +1) unless already watching that id with no navigate-start in between (then nothing changes); with a non-watch URL → `idle`; pagehide → `idle`. `adShowing`/`hasVideoElement` describe the player, so they survive every phase change. **Decided: generation rises on every entry into `watching`, including a navigation back to the same id** (it went through `leaving`, which dropped the snapshot): W15 treats a changed generation as a fresh start and discards older replies. **`src/page.js`** (adapter, injected `document`/`window`/`MutationObserver`): `watchPage({document, window, onChange, MutationObserver?})` → `{state, video, stop()}`; listens for `yt-navigate-start`/`yt-navigate-finish` on the document and `pagehide`/`pageshow` on the window; reads `location.href` only at start, at navigate-finish and at a bfcache `pageshow` (added: `persisted` → `initial` again), never polls. The element (`video.html5-main-video`) and player (`#movie_player`) are looked up at start; a document `MutationObserver` (childList+subtree) runs **only while either is missing**; once found they are kept and only rechecked (`isConnected`) at navigate-finish, so a replacement is reported as `present:false` then `true` (a player that left clears the ad). Ads: a MutationObserver on the player's `class` attribute. Order at start: elements first, then `initial`, so watching begins with the element known; `onChange` can fire before `watchPage` returns. `stop()` removes all four listeners and both observers, idempotent, and ignores callbacks already queued. **`src/content.js`** starts the watcher, exports `currentPageState()` and `watcher`; `localStorage.ffDebug = '1'` (YouTube's page storage) logs each change with `console.debug`, otherwise silent. **For W13–W15**: key everything on `generation`; on `shouldMute` close the gain and drop the snapshot; act only when `isFilterable`; W14 must still wait for `loadedmetadata`/`playing` after a `watching` change (the element's source changes after navigate-start, ADR-0007); `watcher.video` can go stale between navigations, check `isConnected`. **Open (W15/W16)**: on a non-watch page the phase is `idle`, but YouTube's **miniplayer keeps playing the previous video in the same element** - unfiltered unless W15 fails closed there (e.g. mute while idle and the element is playing); and an ad's `ad-showing` class is still unverified in stock Chrome (W16). Tests 719 (+279: 85 video-id, 121 page-state, 73 page with a fake EventTarget document/window and a hand-fired MutationObserver) |
| W13 | Playback Gate | ✅ | `task/W13-playback-gate` | `6559ce1` | Four new content-script modules directly in `src/` (not wired yet: W15). **`src/coverage.js`** (pure): `normaliseCoverage` (drops non-finite/empty/reversed intervals, sorts, merges overlapping or touching within `TOUCH_TOLERANCE` 1e-6), `coveredRunEnd(cov, pos)` (end of the run containing pos, closed intervals, else null), `coveredAhead(cov, pos)` (0 when uncovered or at a run's end; **negative position counts as 0**, NaN/Infinity → 0), `contains(cov, from, to)` (one run holds the whole span; point spans allowed; reversed → false; no clamping). **`src/gate.js`** (pure): `decideGate(input)` → frozen `{action: hold/release/none, reason, heldByUs, resume}`; `gateOverlay(input, decision?)` → `{visible, title, detail, percent, showUnfiltered}` (`HIDDEN_OVERLAY` unless `hold`); `mediaAhead`, `effectiveRate`, `HOLD_SECONDS` 8, `RESUME_SECONDS` 30, `END_TOLERANCE` 0.5. Input: `{page (PageState), view (WatchView or null), error ({kind,status?,detail?} or null), unfiltered, failPolicy, position, playbackRate, duration, paused, ended, heldByUs, userPaused, playRequested, serverUrl}`. **Decided: thresholds are wall-clock**, so media seconds needed = threshold × rate (hold < 16 s / release ≥ 60 s at 2×, 4 s / 15 s at 0.5×); strict `<` to start a hold, `≥` to release, keep the current state in between (hysteresis); a non-positive/non-finite rate counts as 1. **End**: `complete` = covered to the end whatever the coverage (never compared with the duration, per W10's cached-coverage note); otherwise a covered run containing the playhead that ends within 0.5 s of the duration (the element's, else the view's; never required) counts as reaching the end. **Precedence** (first wins): leaving → none; ad → none; not filterable → none (`not-watching`); ended → none; unfiltered → release/none; error → hold `error` (fail-closed) or release/none `fail-open`; no view, or a view whose `videoId` is not the page's → hold `starting`; complete / covered to end → release/none; else coverage with hysteresis, holding with reason `failed`/`unsupported` for those states (fail-open lets go), `not-keeping-up` when `keepingUp` is false, else `preparing`. `release` vs `none`: release = drop our hold and play again if `resume` (= `!userPaused`); none = drop it without playing (ads, navigation, end), and nothing when not held. **Also decided**: a failed session's coverage still counts (a mid-way failure plays the covered stretch, holds at its end); `cancelled` holds like queued (the next POST replaces it); server-side error kinds (`unreachable`, `timeout`, `http`, `bad_response`, `extension`) are ignored once the view is `complete` (all Hits known), but kind **`audio`** (W14/W15: `createMediaElementSource` refused, ADR-0007) always holds; a user-paused video with short coverage is still held (so a play press is caught) but never played on release; a play press during a starting/preparing/not-keeping-up hold → reason `still-preparing` (failure reasons are kept). Overlay: percent is progress towards the resume threshold (0–99, floor) while `transcribing` only, else null (whole-video progress would read ~1% on a long video); per-state detail; `failed` gets `failure_kind` advice plus the server's reason in brackets (no kind → the reason alone); `unsupported` gives the server's reason (fallback about livestreams); errors use `connection.js`'s `describeError` with per-kind titles; "You paused the video…" line when `userPaused`; `showUnfiltered` on every visible overlay. **`src/gate-controller.js`** (adapter): `createGateController({video, now?, onPlayWhileHeld?, onUserPause?, onPlayError?})` → `{apply(decision) → 'paused'/'held'/'played'/'released'/'nothing', heldByUs, userPaused, playRequested, stop()}`. Hold pauses only a playing video (a video already paused is held with `userPaused` true, so release will not start a video nobody started); repeated holds call nothing; release plays only if it paused the video itself or play was pressed during the hold, and the user has not paused since, and `decision.resume` is not false; `none` forgets the hold. Its own `pause()`/`play()` events are recognised by an expectation that lasts `SELF_EVENT_MS` (1 s) and is consumed by one event. A play press during a hold is **paused again at once by the controller** (no audio leak while W15 re-decides) and reported; `AbortError` from a play() interrupted by our pause is swallowed, other refusals (autoplay `NotAllowedError`) go to `onPlayError`. **`src/overlay.js`** (adapter): `createOverlay({document, onUnfiltered})` → `{host, render(model, player), destroy()}`: host `#foulfilter-gate-overlay` appended to `#movie_player` (moved when the player changes, removed for null), `position:absolute; inset:0; pointer-events:none`, closed shadow root, styles via `style.setProperty(..., 'important')` only (no `<style>`/style attribute, in case of page CSP) with `all: initial` on the panel; only the button takes pointer events; its click is `stopPropagation`ed (YouTube would toggle play). **For W14/W15**: build the gate input on every heartbeat reply, page-state change and media event (`play`, `pause`, `seeked`, `ratechange`, `durationchange`, `ended`) and on the controller's callbacks; feed back `controller.heldByUs/userPaused/playRequested`; one controller per `<video>` (stop it when `watcher.video` changes); pass `duration: video.duration`; reset `unfiltered` and the controller's hold per `generation`; "Watch unfiltered" should also call `video.play()` itself inside the click (a user gesture; release does not play a user-paused video); pass `error` for a failed heartbeat (keep the last view: a complete view survives server errors) and `{kind: 'audio'}` when the graph cannot be built; treat settings `enabled: false` as `unfiltered`; `failPolicy` from settings. **Open (W16)**: the overlay's look and CSP behaviour, and whether YouTube's own autoplay fights a hold, are unverified in a real browser. Tests 1143 (+424: 68 coverage, 254 gate, 68 gate-controller with a fake EventTarget video and hand-moved clock, 34 overlay with a fake document) |
| W14 | Live Censoring | ✅ | `task/W14-live-censoring` | `111477d` | Three new content-script modules directly in `src/` (not wired yet: W15). **`src/schedule.js`** (pure): `planSchedule({hits, currentTime, playbackRate, audioNow, horizon=2, offsetMs=0, playing})` → frozen `{audioNow, closedNow, events: [{time, gain: 0|1}], key}` (events ascending, alternating, all after `audioNow`; the first is an open when `closedNow`); `isInsideHit(hits, t)` (`start <= t < end`), `validHits`, `samePlan(a, b, tol=0.01)`, `HORIZON_SECONDS` 2, `MERGE_GAP_SECONDS` 0.02, `PLAN_TOLERANCE_SECONDS` 0.01. Mapping `audioNow + (t - currentTime)/rate + offsetMs/1000` (offset in wall-clock seconds, not scaled by rate; positive = later). **Decided**: every valid Hit is mapped to the audio clock first, then sorted and merged when overlapping or < 20 ms apart **on the audio clock** (so the 5 ms ramps never overlap), over all Hits, so a touching chain reaching past the horizon opens only at its end; a merged span is included when it starts ≤ horizon audio seconds ahead (inclusive, 1e-9 slack for float noise) and ends after now; its open is always scheduled even beyond the horizon; a span already started → `closedNow` (the "close now / clamp past times" rule), one already ended is dropped. **Not playing** (flag not `true`, rate 0/negative/NaN/∞, playhead or audio clock not finite) → no events, `closedNow = isInsideHit(hits, currentTime)` without offset, so a paused-inside-a-hit video stays closed and a resume cannot leak. Invalid Hits (non-object, non-finite, `end <= start`) ignored; bad horizon → 2, bad offset → 0. Dedupe: `key` lists the events to the ms without `audioNow`, but the controller compares with `samePlan` (10 ms tolerance) because `currentTime` jitters by one render quantum (2.7 ms). **`src/audio-graph.js`** (adapter): `createAudioGraphs({createContext})` → `{attach(video) → {ok:true, graph} | {ok:false, error}, context}`; `getPageAudio(createContext = () => new AudioContext())` is the module-level page singleton (the context is made on the first `attach`, then never closed). Per element one `createMediaElementSource`, results remembered in a WeakMap; `InvalidStateError` → `{kind:'audio', reason:'already-connected', detail:'…Reload the page to filter it.'}` (remembered, never retried); another throw → `reason:'failed'` (not remembered); context factory throws → `reason:'no-context'`. Graph: source → programme gain → destination; one page oscillator (sine 1 kHz, started once) → unity split → per-video bleep gain → destination. `graph.apply(plan, method)`: `cancelScheduledValues(plan.audioNow)` on both gains, set the value now, then per event: a close ramps linearly 1→0 over the **5 ms before** its time (start clamped to `audioNow`), an open ramps 0→1 over the **5 ms after** it, so the gain is fully 0 over the planned span; the bleep gain mirrors (level × (1 − programme)) at the same instants. `muteNow()` (programme 0, bleep 0) / `openNow()` (1, 0) are steps at `context.currentTime`; `resume()` resolves to the state, never rejects; `currentTime`, `state`. **Decided**: `BLEEP_LEVEL` 0.125 = FFmpeg `sine`'s default amplitude, which is what `FilterGraph.Bleep` mixes; `RAMP_SECONDS` 0.005; the programme gain starts **closed** until told; unknown method = silence; `muteNow` never bleeps. **`src/censor-controller.js`** (adapter): `createCensorController({video, graph, hits, method, offsetMs, mode, horizon, setInterval, clearInterval})` → `{setHits, setMethod, setOffset, setMode('filter'|'mute'|'open'; unknown → mute), refresh(), mode, plan, applyCount, ticking, stop()}`; `TICK_MS` 100, `MEDIA_EVENTS`. Schedules only when `!paused && !seeking && readyState >= 3` and not stalled; `seeking`/`waiting`/`emptied` stall it until `seeked` or `playing` (**decided: `seeked` also ends a stall**, since a seek inside the buffer fires no `waiting`/`playing`; readyState still gates it); `play`, `pause`, `ratechange`, `timeupdate`, `ended`, `loadedmetadata` just re-plan. Every event re-plans, so a stop applies an empty plan at once (= cancel). The tick runs only while filtering and really playing. A plan is applied only when `!samePlan` with the last applied or the method changed; `mute`/`open` are told once per mode change and schedule nothing; back to `filter` always re-applies. `stop()` removes listeners and the tick and **leaves the gain as it is**. **For W15**: call `getPageAudio().attach(video)` from the first `play` event (or the overlay's click) and `graph.resume()` there; on `{ok:false}` pass `result.error` to the gate as `error` (kind `audio` always holds); if `graph.state !== 'running'` after resume, show a click-to-enable overlay (W16 verifies in stock Chrome); one controller per `<video>` (stop it and create another when `watcher.video` changes; the graph for a reused element is the same object); `setHits(view.hits)` only when a view is not `unchanged`, and `setHits([])` on a new generation; mode = `mute` when `shouldMute` (leaving) or not filterable/idle-with-miniplayer or while the gate holds for an error, `open` for ads, "Watch unfiltered", `enabled:false`, `filter` otherwise; `setMethod(settings.censorMethod)`, `setOffset(settings.offsetMs)` on settings changes; call `controller.refresh()` after `resume()`. Tests 1487 (+344: 148 schedule, 86 audio-graph with a recording fake AudioContext, 110 censor-controller with a fake EventTarget video, recording graph and hand-run intervals). Not run in a real browser (W16) |
| W15 | Wiring and failure policy | ✅ | `task/W15-wiring` | HASH | Two new content-script modules directly in `src/` (manifest integrity green). **`src/session.js`** (pure reducer): `reduceSession(state, event)` (same object when nothing changed, else frozen), `INITIAL_SESSION`, and derivations `deriveSession(state, media)` → `{input, gate, mode, hits, method, offsetMs, overlay, badge, miniplayer}`, `gateInput` (exactly the 14 W13 fields; `unfiltered` = clicked or `enabled:false`), `gateError`, `shouldHeartbeat`, `heartbeatCadence`, `nextHeartbeatAt`, `heartbeatDue`, `heartbeatRequest`, `isMiniplayerMuted`; `media` = `{position, playbackRate, duration, paused, ended, heldByUs, userPaused, playRequested}` read from the element and gate controller at derive time (never stored). State: `page`, `generation`, `videoId`, `view` (last good), `hits` (kept by identity across `unchanged`), `session`/`revision` (for `since`), `lastError`, `consecutiveErrors`, `unfiltered`, `settings`, `settingsLoaded`, `audio` {detached/running/suspended/failed, error} (per **element**, survives navigation; reset by `audio-detached`), `inflight` {generation, seq, at}, `nextSeq`, `lastSentAt`, `lastPosition`. Events: `page`, `settings`, `unfiltered`, `audio` (ok+state / !ok+error), `audio-detached`, `heartbeat-sent` {at, position}, `heartbeat-reply` {generation, seq, result}, `tick` {at}. **Decided**: a new generation *or leaving `watching`* forgets view/hits/since/errors/unfiltered/inflight/cadence; a reply counts only if generation **and** seq match the one in flight (stale and duplicate replies are no-ops); an `unchanged` reply from another session or with no Hits held is dropped and `since` forgotten (next asks in full); a view for another video = `bad_response`. **Cadence**: 1 s while working (also during a hold, while paused, during an ad, before the element is found); **5 s** once `complete`/`failed`/`unsupported` (keeps a complete session alive against the 2 min idle timeout, picks up a Bad Words edit, and retries a failed one after the server's 1 min retention); none when disabled, unfiltered, idle/leaving, settings not loaded yet, or the audio `failed` (a later successful attach resumes them). One in flight at a time; one lost for `INFLIGHT_TIMEOUT_MS` 10 s is a `timeout` error. During an ad the playhead sent is the last one sent before it. **Error tolerance**: a failed heartbeat reaches the gate only with no view yet, after `ERROR_TOLERANCE` 3 in a row, or at once when covered-ahead < 8 s × rate; until then the last view stands (its coverage is final, so playing the covered stretch is safe); the gate itself ignores server errors once `complete`; the audio error always goes through and wins. **Suspended context** → the session overrides the gate with `{action:'hold', reason:'audio-suspended'}` (when enabled, filterable, not ended; even if unfiltered, since the context carries the sound) and the overlay "Click to enable FoulFilter" with an **Enable sound** button (overlay.js gained `showEnable`/`onEnable`, `ENABLE_LABEL`; the second button comes after Watch unfiltered). **Censor mode**: `open` when disabled / ad / unfiltered; `mute` when not watching (leaving, idle), while the gate holds (the video is paused; a play press cannot leak), and with no element; else `filter`. **Miniplayer (W12 open problem)**: idle + element playing + graph attached → `mute`, overlay note "FoulFilter muted the miniplayer" (no unfiltered button), badge `MUTE`; filtering it with the last Hits was rejected (nothing proves the element still plays that video; a wrong guess is audible); cost: the miniplayer is silent. `enabled:false` → `open`, no heartbeats, no overlay, no hold, never attaches a graph. **Badge** (`BADGE_COLORS`): `OFF` disabled/unfiltered, `''` idle/leaving, `MUTE` miniplayer, `AD`, `…` starting/preparing/still-preparing/no element (blue), `!` error/failed/unsupported/not-keeping-up/audio-suspended/audio error (red), `✓` filtering (green); tooltip = overlay title (+percent) or "filtering (x% prepared)". **Protocol**: `ff/badge` `{text ≤ 4 characters (code points), color #rrggbb, title ≤ 300}` (`badgeMessage`, `BADGE_TEXT_MAX`, `BADGE_TITLE_MAX`); `relay.handle(raw, sender)` applies it with an injected `action` (`chrome.action.setBadgeText/BackgroundColor/setTitle`) on `sender.tab.id`, refusing a sender with no tab (`invalid_request`) and turning a throwing API into `extension`; background.js passes `chrome.action` and `sender`; no new permission. **`src/content-app.js`** (adapter, everything injected): `startContent({document, window, send, settings, audio, watchPage?, createOverlay?, MutationObserver?, now, setTimeout/clearTimeout, setInterval/clearInterval, log?})` → `{state, derived, watcher, overlay, censor, gate, clickUnfiltered, clickEnable, stop}`. `dispatch` → `sync` (not re-entered; a nested event runs one more pass): rebinds when `watcher.video` changes or disconnects (old gate/censor stopped, old gain `muteNow`, `audio-detached`), attaches the graph on the element's `play` (or at once if already playing and still `detached`) only while enabled and watching, `await graph.resume()` → `audio` event → `censor.refresh()`; pushes only *changed* hits/method/offset/mode to the censor controller; `gate.apply`; overlay into `#movie_player`; badge only when changed; one heartbeat `setTimeout` for `nextHeartbeatAt` (fires: `tick`, then send if due, tagged generation+seq). Gate re-decides on `pause playing seeked ratechange durationchange loadedmetadata timeupdate ended emptied` + the controller callbacks. Watch unfiltered: `unfiltered` event, `resume()`, `video.play()` inside the click. Enable sound: attach or resume, then `video.play()` (caught under the hold, played on release). **`src/content.js`** is now just the bootstrap (real messenger, settings store, `getPageAudio()`), exporting `app`, `watcher`, `currentPageState`. Tests 1958 (+471: 315 session, 96 content-app end-to-end with a fake page/video/messenger/clock/audio running navigate → hold → views → release → hits scheduled → server down → navigate away → miniplayer mute, stale replies, ads, suspended context, replaced element, settings live; 45 ff/badge protocol + relay; 15 overlay Enable sound). **W16 must verify in stock Chrome**: autoplay on a fresh tab (does `play` fire, is the context suspended, does Enable sound / a play press resume it, does Chrome let `video.play()` run after the click's `await`); the miniplayer really is `video.html5-main-video` inside `#movie_player` and the overlay note fits it; YouTube's home-page **inline previews** are not picked up as the main element (if they are, a muted hover preview would be treated as the miniplayer); ads (`ad-showing`, `currentTime` during the ad, heartbeat position after it); the badge per tab and that a full navigation clears it; a background tab (1 s timer throttled to ≥ 1 s is fine; the 100 ms censor tick is what matters); the extension reloaded under an open tab (orphaned script → `extension` errors → hold after 3; reload → `already-connected` → "reload the page"); overlay look and click-through; YouTube's own autoplay-next fighting a hold |
| W16 | End-to-end harness and checklist | — | | | |
| W17 | Time-to-first-play | — | | | |
| W18 | Documentation | — | | | |

## Completed outside the ledger

| What | Commit | Notes |
|---|---|---|
| Docs: plan, analysis, breakdown, parallelisation review | `cdcd2f8`…`3fc3e06` | |
| Media fixtures + generator | `6bdeda6` | 7 fixtures, exact ground truth in `manifest.json` |
| `appsettings.json` with the feature flags | `eaf26ee` | The docs and `.gitignore` both described a file that did not exist; `Storage` deliberately excluded, see the flag block |
| Root `.gitignore` + `.gitattributes` | `6bdeda6`, `a03fdd8` | |

## Container dropped (after T34)

T32 wrote a `Dockerfile`, `docker-compose.yml`, `.dockerignore` and
`.env.example`, but no Docker daemon was running and the image was never built.
Once T31 and T34 had shown the app running natively on the RTX 3080 Ti - the CLI
in 16 s cold, the evaluation harness across all seven fixtures in 12.7 s - a
container bought nothing on the target machine: on Windows it could only reach
the GPU through WSL2 and the NVIDIA container toolkit, a harder path to the same
card. The user chose to drop it, and the files were deleted rather than left as
untested configuration that looks supported.

What survives from T32 is everything that was not container-specific: the legacy
variable mapping (an old `.env`'s values still work when set in the
environment), the per-user data directory, and the CLI sharing Web's
`appsettings.json`. If the app ever needs to run on Linux or as a hosted service,
the deleted files and T32's list of untested assumptions are in git history at
`7f04e44`.

## API key hardening (after T33)

T33 reported that user-secrets never worked. Following it up found a real hole
behind the docs problem: `AddSmartCut` looked the key up as
`configuration["GOOGLE_API_KEY"]` before the environment, and configuration
merges every JSON file the host loads - so a key pasted at the top level of the
committed `appsettings.json` switched Smart Cut on and would have travelled
with the repository. The existing guard only covered the guessed spelling
`SmartCut:GoogleApiKey`.

The configuration lookup is gone; the key comes from the `GOOGLE_API_KEY`
environment variable only. A test now pins the exact-name case
(`IgnoresTheKeyEvenUnderItsOwnNameInConfiguration`), written red first. The
lookup was not rewired to user-secrets: none of it worked, and a narrower
provider-filtering scheme would be more code guarding a feature nobody uses.

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

## T34 output — the evaluation harness, and what it measured

`foulfilter-eval` (`src/FoulFilterNet.Evaluation`, tests in
`tests/FoulFilterNet.Evaluation.Tests`) ports the scoring half of
`eval_misses.py`. It runs every fixture in `tests/fixtures/media/manifest.json`
through the real `MediaPipeline` (the CLI's composition, `Render = false`) and
scores it at two levels:

- **Raw words**: the words the pipeline persisted to its transcript store, read
  back and matched with `PhraseMatcher.FindHits`. This is what ADR-0006
  measured. Margins assume the 0.15/0.25 s padding still to come.
- **Final hits**: `JobSummary.Hits`, which are reconciled, padded, merged and
  Smart Cut-refined if enabled. This is what actually gets censored. No further
  padding is assumed.

Only `FoulFilterNet.slnx` was touched among the shared files (two project
entries, as authorised).

### How it scores

- **Scoring is pure.** `BoundaryScorer.Score(FixtureTruth, IReadOnlyList<Hit>,
  ScoringRules)` needs no FFmpeg, model or GPU, and the 91 unit tests live there.
  `ScoringRules.RawWords` and `ScoringRules.FinalHits` differ only in the padding
  still to apply.
- **Pairing**: each planted span goes to the nearest report of the same phrase,
  greedily over all eligible pairs, nearest first by |Δstart| + |Δend|. A report
  is eligible only within 1.0 s of the span; further away it is a false positive
  plus a miss, not one huge error. A merged window (`damn+go to hell`) can be
  claimed once per phrase it carries. This is what pairs the five repeats in
  `repeated_hits.mp3` correctly.
- **Signs**: `dStart` = reported − truth, so **positive is a late start**.
  `dEnd` = reported − truth, so **negative is an early end**. These are the two
  directions that leave speech audible. `margin` is the smaller of the two
  sides' slack, and a negative margin is how much would be heard. `covered`
  means both margins are >= 0.
- **Two denominators.** The manifest records where words are, not whether they
  are profane, so `false_positive.mp3` is named innocent by the harness
  (`--innocent`, default that file). *Detection* and every boundary figure count
  all 11 spans (as ADR-0006 did). *Recall* counts the 10 profanities.
  *Precision* is reports paired with a profanity over all reports, so the "hoe"
  hit is a false positive, marked expected.
- **Magnitude "within tolerance"** (|dStart| <= 0.15, |dEnd| <= 0.25) is kept
  because it is ADR-0006's "10/11 starts" column. At the final level it means
  little, since padding moves every window by design; read `covered` and
  `margin` there.

### Running it

```powershell
$env:Transcription__ModelDirectory = 'F:\SourceCode\FoulFilterNet\models'
$env:Transcription__Model = 'large-v3-turbo'
$env:Transcription__Language = 'en'      # optional: auto-detect gave identical results
dotnet run --project src/FoulFilterNet.Evaluation -- --work <scratch dir>
```

Options: `--manifest`, `--work` (default: a new folder under `%TEMP%\foulfilter-eval`),
`--json` (default `<work>/evaluation.json`), `--bad-words` (default: exactly the
manifest's phrases, written into `<work>`), `--innocent`, `--transcripts`
(default `<work>/transcripts`; point it at an existing cache to resume instead
of transcribing), `--rescan`. The JSON has `run` (model, language, device, the
runtime that actually loaded, Smart Cut, timings), `rawWords` and `finalHits`
(each a `summary` plus every span), and `timings`, so two runs can be diffed.
Nothing is ever downloaded.

`RUN_GPU_TESTS=1` also runs `TheRealPipelineAgainstTheFixtures` (10 facts, one
shared run), which pins the ADR-0006 figures below as an acceptance test.

### Measured results (large-v3-turbo, Cuda runtime, 2026-09-17)

Raw words:

```
fixture              phrase            truth           reported                   dStart    dEnd covered  margin
single_hit.mp3       damn              3.410-3.897     3.480-3.940                +0.070  +0.043 yes      +0.080
phrase_hit.mp3       go to hell        3.505-4.227     3.360-4.120                -0.145  -0.107 yes      +0.143
repeated_hits.mp3    damn              0.000-0.487     0.050-0.520                +0.050  +0.033 yes      +0.100
repeated_hits.mp3    damn              2.202-2.689     1.960-2.720                -0.242  +0.031 yes      +0.281
repeated_hits.mp3    damn              4.651-5.138     4.560-5.180                -0.091  +0.042 yes      +0.241
repeated_hits.mp3    damn              6.790-7.277     6.700-7.300                -0.090  +0.023 yes      +0.240
repeated_hits.mp3    damn              8.925-9.412     8.820-9.420                -0.105  +0.008 yes      +0.255
false_positive.mp3   hoe (innocent)    2.634-3.082     2.560-2.960                -0.074  -0.122 yes      +0.128
sample_video.mp4     damn              2.941-3.428     2.800-3.460                -0.141  +0.032 yes      +0.282
audiobook.m4b        damn              5.580-6.067     5.500-5.860                -0.080  -0.207 yes      +0.043
audiobook.m4b        go to hell        11.252-11.974   11.180-11.860              -0.072  -0.114 yes      +0.136
detection 11/11 (1.000)   recall 10/10 (1.000)   precision 10/11 (0.909)   false positives 1 (1 expected)
|dStart| mean 0.105 max 0.242 signed mean -0.084   within 0.15 s: 10/11
|dEnd|   mean 0.069 max 0.207 signed mean -0.031   within 0.25 s: 11/11
worst late start +0.070   worst early end +0.207   covered 11/11   worst margin start +0.080 end +0.043
```

Final hits:

```
fixture              phrase            truth           reported                   dStart    dEnd covered  margin
single_hit.mp3       damn              3.410-3.897     3.330-4.190                -0.080  +0.293 yes      +0.080
phrase_hit.mp3       go to hell        3.505-4.227     3.210-4.370                -0.295  +0.143 yes      +0.143
repeated_hits.mp3    damn              0.000-0.487     0.000-0.770                 0.000  +0.283 yes       0.000
repeated_hits.mp3    damn              2.202-2.689     1.810-2.970                -0.392  +0.281 yes      +0.281
repeated_hits.mp3    damn              4.651-5.138     4.410-5.430                -0.241  +0.292 yes      +0.241
repeated_hits.mp3    damn              6.790-7.277     6.550-7.550                -0.240  +0.273 yes      +0.240
repeated_hits.mp3    damn              8.925-9.412     8.670-9.670                -0.255  +0.258 yes      +0.255
false_positive.mp3   hoe (innocent)    2.634-3.082     2.410-3.210                -0.224  +0.128 yes      +0.128
sample_video.mp4     damn              2.941-3.428     2.650-3.710                -0.291  +0.282 yes      +0.282
audiobook.m4b        damn              5.580-6.067     5.350-6.110                -0.230  +0.043 yes      +0.043
audiobook.m4b        go to hell        11.252-11.974   11.030-12.110              -0.222  +0.136 yes      +0.136
audiobook.m4b        damn              (no plant)      4.570-5.283                               FALSE+
detection 11/11 (1.000)   recall 10/10 (1.000)   precision 10/12 (0.833)   false positives 2 (1 expected)
|dStart| mean 0.225 max 0.392 signed mean -0.225   within 0.15 s: 2/11
|dEnd|   mean 0.219 max 0.293 signed mean +0.219   within 0.25 s: 4/11
worst late start 0.000   worst early end -0.043   covered 11/11   worst margin start 0.000 end +0.043
```

The final-level start margin of 0.000 is `repeated_hits.mp3`'s first word, which
starts at 0.000 s; the window is clamped to the file start and nothing can
precede it.

**Run time.** The first GPU run took 12.7 s wall clock for all seven fixtures,
model load included (2.7 s for the first fixture, 1.3-1.7 s for each after).
The GPU was shared with a game during later runs, and those varied from 67 s to
328 s, with single fixtures stalling for up to 130 s. That is contention, not
the harness. The same run on `Transcription__Device=Cpu` took 220.6 s (about
31 s per fixture).

### Did it reproduce ADR-0006? Yes on the CPU; on CUDA, all but one figure

- **CPU: exactly.** 0.107 / 0.242 s start, 0.069 / 0.207 s end, 10/11 and 11/11
  inside the padding, worst late start 0.070 s (margin 0.080), worst early end
  0.207 s (margin 0.043), 11/11 covered, "hoe" reported once.
- **CUDA: every figure but the start mean, 0.105 instead of 0.107.** The cause
  is one word: the fourth "damn" in `repeated_hits.mp3` starts at 6.700 s on
  CUDA and 6.680 s on the CPU. The ADR measured on the CPU, and its addendum's
  "to the millisecond" was checked on `single_hit.mp3` alone. The backends are
  close but not bit-identical. Neither the harness nor the ADR's conclusion is
  wrong. A dated addendum in ADR-0006 records it, and the opt-in test allows
  exactly that difference (0.0025, which absorbs floating-point rounding).
- **Wording, not numbers:** ADR-0006's "11 planted profanities" are ten
  profanities and the garden hoe. The addendum says so too.

### A finding at the final level: one extra window in the audiobook

The pipeline censors `audiobook.m4b` at 4.570-5.283 s ("thought, a") as well as
at the real "damn". The segment is 3.00-7.30 s, and interpolating by character
offset puts the Candidate at about 4.72 s. DTW puts the word at 5.50 s, which
is 0.78 s away, beyond `HitReconciler`'s 0.40 s proximity tolerance (T20). So
the Candidate is kept at its estimate as well, and the pipeline logs "No aligned
timestamps for 'damn'". This over-censors about 0.7 s of speech and never
leaks. **Not fixed here**: it is T20's reconciliation rule, a behaviour change
with its own trade-off (a wider tolerance lets one word's hit suppress a
neighbouring Candidate). Worth its own task, and this harness is the way to
measure the fix.

### Not done

- No real long-form corpus was measured. The harness takes any `--manifest` in
  the same shape, but only the seven synthetic fixtures exist.
- The Rescan Pass and Smart Cut were not evaluated (`--rescan` and
  `SmartCut__Enabled` are wired but were not run; Smart Cut needs an LLM).
- No committed baseline JSON. The report contains absolute paths and timings,
  so a baseline belongs in a scratch directory; the tables above are the record.

## T33 output — documentation, and where the docs had drifted from the code

The repository root now has a [README.md](../README.md) for the .NET solution
(there was none — the Python's stays at `Legacy/README.md`) and a
[CONTEXT.md](../CONTEXT.md) carrying `Legacy/CONTEXT.md`'s vocabulary forward
with what the .NET design changed. [README.md](README.md) here indexes both and
`adr/`. ADR-0006 was already written by T13; it was re-read and needed no
correction.

**Verified on this host**: `dotnet build FoulFilterNet.slnx` (0 warnings) and
`dotnet test FoulFilterNet.slnx` (1083 passing, 13 skipped, unchanged);
`foulfilter --help` (the documented flags are the real ones); the CLI with no
weights exits 1 with the "No Whisper weights" message and downloads nothing;
`dotnet run --project src/FoulFilterNet.Web -- --urls ...` with a scratch
`DATA_DIR` answered `/health`, `/config` and `/` with 200, and logged its content
root as `src/FoulFilterNet.Web` (the launch profile) - whereas `dotnet run` of
the CLI keeps the caller's working directory, so its default `models` resolves
against the repository root. **Not verified**: anything Docker (no daemon, no
image pulls), a GPU job through Web, the opt-in test gates, and every model size
other than `large-v3-turbo` (the README marks the rest approximate).

Discrepancies found - all fixed in the docs, no code changed:

1. **User-secrets do not work.** The porting plan, the Stream D notes below and
   a comment in `SmartCutServiceCollectionExtensions` say the Google API key can
   come from user-secrets. `AddSmartCut` does consult configuration for
   `GOOGLE_API_KEY`, but neither host declares a `UserSecretsId`, so
   `dotnet user-secrets list --project src/FoulFilterNet.Web` fails with "Could
   not find the global property 'UserSecretsId'", and the CLI's host is never in
   the Development environment anyway. The README documents the environment
   variable as the only working source. Making user-secrets real is a one-line
   csproj change for whoever wants it (not done: docs task).
   *Resolved after T33 - see "API key hardening": the configuration lookup was
   itself the problem, and was removed rather than wired to user-secrets.*
2. **An unaligned Candidate is not dropped.** The T21 notes said a candidate the
   aligner never placed "is logged and dropped rather than censored at a guessed
   timestamp". The code does the opposite, correctly: `HitReconciler` falls back
   to the Candidate's segment estimate (finding 2), and `MediaPipeline` logs "No
   aligned timestamps ... using the segment estimate". Corrected in place below.
3. **The open question "CUDA has never actually run on this host"** was still
   listed after the T13 addendum recorded CUDA as verified. Struck through below.
4. **T31's configuration notes are superseded** by T32 (appsettings read from
   beside the executable, not the working directory; the transcript cache default
   is the per-user data folder, not `/data/transcripts`). Left as history, since
   T32's section says so; the README describes the current behaviour only.
5. **The porting plan's dependency diagram** shows `Pipeline -> Domain` and
   `Media` only. In the project files `FoulFilterNet.Pipeline` also references
   `Transcription` and `SmartCut`; `MediaPipeline` uses the static
   `RescanPass` and `MediaEditor.ResolveOutputPath` (its engines are still only
   interfaces), and no source file in Pipeline uses the `SmartCut` project at
   all. `Jobs` references `Pipeline`, and Web reaches the engines transitively
   through `Pipeline`. `CONTEXT.md` has the
   actual reference table; the plan is left as the plan.
6. **`Transcription:ModelDirectory` "resolved against the process directory"**
   (the XML docs on `TranscriptionOptions` and `WhisperModelSource`) means the
   working directory - `Path.GetFullPath` - not the executable's directory,
   which is where the CLI reads `appsettings.json` from. The README says working
   directory.
7. **"Never from appsettings" is a convention, not an enforcement.** The key is
   looked up as `configuration["GOOGLE_API_KEY"]` before the environment, and
   configuration includes `appsettings.json`, so a top-level `GOOGLE_API_KEY`
   entry in a JSON file *would* be read. Nothing in the repository does that; the
   README says to use the environment and never a committed file, rather than
   claiming the file is ignored.

## T32 output — what T33 and whoever deploys need to know

Configuration is `appsettings.json` first, environment second, under both
spellings. The Web app runs natively on Windows and serves the UI (verified).
T32 also wrote container files; they were never built and have since been
deleted - see "Container dropped".

### Legacy variables

`LegacyEnvironmentVariables` in `FoulFilterNet.Pipeline` (the one project both
hosts reference) is a configuration source that Web and the CLI each add after
their defaults. So a legacy variable beats `appsettings.json`, exactly as it beat
the Python's defaults; a `Section__Key` variable for the same key beats the
legacy name, because nobody types the .NET spelling by accident. A **blank**
legacy variable is unset - the legacy compose file's `X=${X}` pass-through set
every missing variable to the empty string, so an old `.env` still behaves, and the Python read nearly all of them with
`os.getenv(X) or default`.

| Legacy variable | .NET key | Translation |
|---|---|---|
| `DATA_DIR` | `Storage:DataDirectory` | as is |
| `TRANSCRIPT_DIR` | `Storage:TranscriptDirectory` | as is |
| `BAD_WORDS_PATH` | `Storage:BadWordsPath` | as is |
| `MAX_UPLOAD_MB` | `Storage:MaxUploadMegabytes` | as is |
| `CENSOR_METHOD` | `Cli:CensorMethod` | as is; the CLI still accepts `delete`. Web never read it, in the Python or here |
| `WHISPER_MODEL` | `Transcription:Model` | as is; `TranscriptionOptions` normalises |
| `WHISPER_LANGUAGE` | `Transcription:Language` | as is |
| `UNLOAD_MODELS_AFTER_JOB` | `Transcription:UnloadAfterJob` | `1`/`true`/`yes` (any case) is true, else false - the Python's set |
| `AI_ENHANCE` | `SmartCut:Enabled` | only `true` (any case) is true - the Python's `== "true"` |
| `AI_MODE` | `SmartCut:Mode` | `local` is `Local`, anything else `Google` - `ai_helper.py`'s branch, so no value crashes the binder |
| `LOCAL_LLM_URL` | `SmartCut:LocalUrl` | as is |
| `LOCAL_LLM_MODEL` | `SmartCut:LocalModel` | as is |
| `GOOGLE_API_KEY` | *(not mapped)* | still read by that name only, by `AddSmartCut`; `SmartCut:GoogleApiKey` stays ignored |

**Retired**, not mapped, and **warned about at startup** by both hosts when set
to anything non-blank (`LegacyEnvironmentVariables.Retired` carries each reason):
`ALIGN_DEVICE` (alignment is part of transcription now; use
`UNLOAD_MODELS_AFTER_JOB`, or `Transcription__Device=Cpu` to move all of
transcription off the GPU - mapping it would have silently made every job ~5x
slower for someone who only wanted the aligner moved), `WHISPER_MULTI_GPU`,
`WHISPER_ATTN`, `ANALYSIS_CHUNK_SIZE` (the Python never read it either),
`TORCH_INDEX_URL`, `HSA_OVERRIDE_GFX_VERSION`, `HF_HOME`, `MIOPEN_USER_DB_PATH`,
`TRITON_CACHE_DIR`. `Transcription:Device` and `Transcription:ModelDirectory`
have no legacy name; use `Transcription__Device` / `Transcription__ModelDirectory`.

One consequence worth knowing: an `appsettings.json` value is beaten by a legacy
variable, but so is a `--Section:Key=value` **command-line** switch, because the
legacy source is added last. Neither host takes configuration switches in
practice (the CLI's arguments go to System.CommandLine), so this was accepted
rather than coupling the translator to the environment provider's type.

### Decisions handed over by T13 and T31

1. **Native data directory.** The code default is now
   `DataLocations.DefaultDataDirectory`: `%LOCALAPPDATA%\FoulFilterNet` on
   Windows (`~/.local/share/FoulFilterNet` on Linux), not `/data`. It is absolute,
   writable without elevation, and the same whichever directory a terminal is
   in, which is what lets the two hosts share a transcript cache. `DATA_DIR`
   moves it. `BadWordsPath` now defaults to
   `bad_words.txt` *inside the data directory* rather than a fixed
   `/data/bad_words.txt`, so moving the data moves the list. `appsettings.json`
   lists every `Storage` key blank, and blank means default.
2. **The CLI shares Web's configuration.** `FoulFilterNet.Cli.csproj` links
   `src/FoulFilterNet.Web/appsettings.json` into its output, and the CLI's host
   reads it from `AppContext.BaseDirectory` instead of the working directory -
   one file, found wherever the terminal is. Its transcript cache is resolved
   from the same `Storage` keys as Web's (`TRANSCRIPT_DIR`, else
   `<DATA_DIR>/transcripts`, else the per-user default), so a file transcribed by
   either host is a Resume hit for the other. The Python CLI ignored `DATA_DIR`;
   honouring it is the deliberate change. `FoulFilterCommandLine.ToRequest`
   now takes a configuration lookup by **.NET key**, not a raw environment lookup.
   `Transcription:ModelDirectory` (`models`) is still resolved against the
   working directory, as T13 left it.
3. **`Whisper.net.Runtime.Cuda12` stays undeclared.** `Whisper.net.Runtime.Cuda`
   1.9.1 is built with the CUDA **13** toolchain and needs a driver that
   supports 13.x (>= 580). The target host reports driver **595.79** on the RTX
   3080 Ti and has already run the Cuda runtime natively, so the 12 build would
   never be selected on this machine. Declaring it would add another ~170 MB
   native library per platform to every build output. If this ever runs on a
   host whose driver predates CUDA 13: declare the package and reference it from
   `FoulFilterNet.Transcription`. Until then Auto quietly falls back to the CPU on such a host,
   and `RuntimeOptions.LoadedLibrary` in the log says so.

### Running natively (Windows)

```powershell
$env:Transcription__ModelDirectory = "$PWD\models"   # reuse the weights at the repository root
dotnet run --project src/FoulFilterNet.Web -- --urls http://localhost:8000
```

Put `bad_words.txt` in `%LOCALAPPDATA%\FoulFilterNet` (or set `DATA_DIR` /
`BAD_WORDS_PATH`). The relative `models` default resolves against the working
directory, and `dotnet run` runs from `src/FoulFilterNet.Web`, hence the
explicit model directory above; without it Web downloads the model there on
first use. Verified on this host both ways - the built DLL, and `dotnet run`
from the repository root (`/` and `/config` both 200, then stopped). With
`DATA_DIR`, `WHISPER_MODEL=small`, `AI_ENHANCE=True`
and `ALIGN_DEVICE=cpu` set, `GET /` served the UI (200) and `GET /config`
answered 200 with `whisper_model: openai/whisper-small` and `ai_enhance: true`
(Local mode needs no key); the data directories were created under `DATA_DIR`,
and the `ALIGN_DEVICE` warning was logged. The CLI loaded the copied
`appsettings.json` and logged the same warning.

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
- **CUDA verified (2026-09-17).** T13 measured on the CPU because the host had no
  CUDA toolkit runtime; with it installed, Whisper.net's loader picks the `Cuda`
  runtime unprompted. `RUN_GPU_TESTS=1` over the Transcription tests passes all
  192 with none skipped in 5.6 s - model load plus all seven fixtures - where
  the CPU needed about 28 s for one 8 s fixture. The CLI's end-to-end run on
  `single_hit.mp3` reproduced ADR-0006's boundaries to the millisecond, so the
  timestamps really are backend-independent. **`Whisper.net.Runtime.Cuda12` is
  still undeclared** in `Directory.Packages.props`; it only matters on a host
  with 12.x drivers, and T32 owns that decision.
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
  `GoogleApiKeySource` delegate that reads the `GOOGLE_API_KEY` environment
  variable and nothing else. *(Originally it consulted configuration first, which
  let a committed `appsettings.json` entry supply the key - fixed, see "API key
  hardening".)* Tests substitute the delegate; nothing logs the key.
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
  placed is logged and censored at its segment estimate (`HitReconciler`,
  finding 2). *(Corrected in T33: this line used to say such a candidate was
  dropped, which the code never did.)*
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
- ~~**CUDA has never actually run on this host.**~~ **Answered 2026-09-17** -
  with the CUDA runtime installed Whisper.net loads `Cuda` unprompted, and the
  GPU tests and the CLI reproduce the CPU boundaries; see the T13 output section
  and ADR-0006's addendum. *(T34: close, not identical; one fixture word differs
  by 20 ms - see the T34 output section.)*
- **`HitReconciler`'s 0.40 s tolerance vs DTW words.** T34 found the audiobook's
  segment-interpolated Candidate 0.78 s from its aligned word, so the pipeline
  censors an extra ~0.7 s of speech there. Over-censoring only; a candidate for
  its own task, measured with `foulfilter-eval`.
