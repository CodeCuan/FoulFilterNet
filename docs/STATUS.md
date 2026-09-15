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
| Tests | 26 passing, 7 skipped placeholders |

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

## Task ledger

Status: `—` not started · `WIP` in progress · `✅` merged to main · `⚠️` blocked

| Task | Description | Status | Branch | Commit | Notes |
|---|---|---|---|---|---|
| T01 | Solution skeleton + all 16 projects | ✅ | `main` | `a03fdd8` | Owns shared files; scaffolds everything |
| T02 | Domain artifacts **+ hoisted contracts** | ✅ | `main` | pending | Interfaces FROZEN - see Contracts below |
| T03 | Tokenizer + Bad Words List | — | | | Stream A |
| T04 | Phrase matching | — | | | Stream A |
| T05 | Hit padding + merging | — | | | Stream A |
| T06 | Smart Cut index mapping | — | | | Stream A; T16 depends on this |
| T07 | Filtergraph builders | — | | | Stream B; watch float formatting |
| T08 | FFmpeg process adapter | — | | | Stream B |
| T09 | Media probing (FFprobe) | — | | | Stream B |
| T10 | Audio preparation | — | | | Stream B; T13 depends on this |
| T11 | Media editor | — | | | Stream B |
| T12 | ASR contracts + rescan shifting | — | | | Stream C |
| T13 | Whisper.net transcriber (CUDA) | — | | | Stream C; only real technical risk |
| T14 | Aligner seam | — | | | Stream C |
| T15 | Smart Cut prompt | — | | | Stream D |
| T16 | Smart Cut response parsing | — | | | Stream D |
| T17 | LLM transports | — | | | Stream D |
| T18 | Smart Cut advisor (flag, default off) | — | | | Stream D |
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

## Open questions

- Whisper.net DTW boundary error vs the wav2vec2 forced aligner it replaces.
  Measured in T13 against the manifest; if it exceeds the 0.15/0.25 s padding,
  T14's seam gets a real implementation. **Not yet answered.**
