# Agent instructions

Orientation for an agent picking this repository up cold. Read this, then
[docs/STATUS.md](docs/STATUS.md) — the ledger is the truth about where the work
is, and it outranks anything remembered.

## What this is

FoulFilterNet censors profanity in audio and video. It transcribes with
Whisper.net (whisper.cpp) on CUDA, matches the transcript against a **Bad Words
List**, and edits the audio with FFmpeg — for files (audiobooks, podcasts,
films) and, through a Chrome extension, for a YouTube video as it plays. It is a
.NET 10 port of a Python tool kept unmodified under [`Legacy/`](Legacy) as the
behavioural reference.

- [README.md](README.md) — how to run everything, and every configuration key.
- [CONTEXT.md](CONTEXT.md) — the domain vocabulary. Hit, Candidate, Coverage,
  Smart Cut, Priority Word Pass and the rest are defined terms; use them.
- [docs/](docs) — the plans (`00`–`05`), the ADRs, and `STATUS.md`.

## Layout

| Project | What lives there |
|---|---|
| `Domain` | Pure types and rules: `BadWordsList`, `PriorityWordList`, `PhraseMatcher`, `HitMerger`/`CutPadding`, `SmartCutMapper`. No I/O. |
| `Transcription` | Whisper.net engine, window planning (`TranscriptionWindows`, `PrioritySubWindows`), the Priority Word Pass, the inference lane. |
| `Media` | FFmpeg and FFprobe: audio preparation, filtergraphs, the editor. |
| `Pipeline` | `MediaPipeline`, the spine that runs a file job end to end; transcript cache; configuration keys and data locations. |
| `SmartCut` | The optional LLM pass that widens or rejects a Hit. |
| `Jobs` | The one-at-a-time job queue and its events. |
| `Sources` | yt-dlp: fetching a web video's audio. |
| `Watch` | A Watch Session: windows on demand, Coverage, scheduling against the playhead. |
| `Web` | The ASP.NET host: upload UI, job and watch endpoints, SSE. |
| `Cli` | `foulfilter`, one file per run. |
| `Evaluation` | `foulfilter-eval`: scores the pipeline against fixture manifests. |
| `extension/` | The Chrome extension (plain ES modules, no dependencies). |

Every `src/X` has a `tests/X.Tests` beside it.

## Commands

```bash
dotnet build -c Release            # TreatWarningsAsErrors is on: a warning fails the build
dotnet test -c Release             # ~3700 pass, ~43 skipped (opt-in), about 15 s
csharpier format .                 # MANDATORY after editing any .cs/.csproj/.props
csharpier check .                  # reports what is unformatted without rewriting
```

`csharpier` is a global .NET tool and is invoked as `csharpier`, **not**
`dotnet csharpier`. Keep formatting-only churn out of unrelated commits: if the
formatter touches files you did not edit, commit that separately.

The skipped tests are opt-in and need real resources. Switch them on with an
environment variable:

| Variable | Turns on |
|---|---|
| `RUN_GPU_TESTS=1` | Real whisper.cpp inference on the GPU. Also set `FOULFILTER_MODEL_DIR` unless the weights are in `./models`. |
| `RUN_WEB_TESTS=1` | Tests that fetch from the network (yt-dlp). |
| `RUN_LIVE_LLM_TESTS=1` | Smart Cut against a real LLM endpoint. |

The extension's tests are Node's own runner: `cd extension && npm test`.

## Running it

```bash
dotnet run --project src/FoulFilterNet.Web      # http://localhost:8000
```

The launch profile sets the Development environment, binds port 8000 (the only
origin the extension's manifest asks for), points `Transcription__ModelDirectory`
at this machine's `models` folder, and enables the crash trace. `appsettings.json`
asks for `large-v3-turbo`.

## Evaluating against the fixtures

This is how a change to detection or timing is judged. Never claim a
transcription change helps without a score.

```bash
export Transcription__Model=large-v3-turbo
export Transcription__ModelDirectory=F:/SourceCode/FoulFilterNet/models
# crosstalk: two speakers talking over each other, 35 clips, 33 planted swears
dotnet run -c Release --project src/FoulFilterNet.Evaluation -- \
  --manifest tests/fixtures/crosstalk/manifest.json \
  --bad-words <a copy of the list> --innocent none.mp3 --work <scratch>/ct
# the original fixtures (default manifest)
dotnet run -c Release --project src/FoulFilterNet.Evaluation -- --work <scratch>/orig
```

Current scores on this machine, to be matched or beaten: crosstalk **29/33
detected, 27/33 covered, 0 false positives, 44.228 s censored**; original
**10/11 detected, 10/11 covered, 10.583 s censored** (the one flagged word is a
deliberately planted innocent). `--transcripts <dir>` reuses transcripts, which
is what makes tuning runs quick; a fresh `--work` always re-transcribes.

Fixtures are generated, not recorded: `tests/fixtures/generate-fixtures.ps1` and
`generate-crosstalk-fixtures.ps1` build them from Windows SAPI voices and
FFmpeg, and each profanity's span is exact by construction. Extend a generator
and regenerate rather than hand-editing a manifest.

## Conventions

- **One task, one branch, one commit, one merge.** Branch `task/<id>-<slug>` off
  the branch you were told to, fast-forward merge back. **Never push** — the
  user pushes.
- **TDD.** The first commit-worthy artifact is a failing test. Tests are dense
  and fact-per-test: the SUT and shared asserts in the constructor, one thing per
  `[Fact]`. Match the surrounding style.
- **Do not edit shared files** (`FoulFilterNet.slnx`, `Directory.Build.props`,
  `Directory.Packages.props`) or change a frozen contract. If you need a package
  that is not declared, say so in your report instead of adding it.
- **Update `docs/STATUS.md` in your task's commit** — your ledger row, and
  anything the next agent needs. Write down what you measured, including what
  you tried and rejected.
- Commit messages say *why*, in the repository's voice, and end with the
  `Co-Authored-By` trailer the session gives you.
- Comments explain the reason a thing is the way it is — a measurement, a
  legacy behaviour, a trap. Do not narrate what the code already says.

## Gotchas

- **Weights are gitignored and never committed.** `models/ggml-*.bin`. A
  relative `Transcription:ModelDirectory` resolves against the *working
  directory*, which for Web is `src/FoulFilterNet.Web`, not the repository root.
  A git worktree has no `models/` of its own — point it at the main checkout.
- **The user's Bad Words List is personal.** It lives in
  `%LOCALAPPDATA%\FoulFilterNet\bad_words.txt`. Copy it to a scratch directory
  when a test run needs it, never print its contents, and delete the copy after.
- **The Priority Word Pass costs about 4× transcription time** and is on by
  default. It re-hears every window in short prompted sub-windows to catch
  swearing under crosstalk (ADR-0008). `Transcription:PriorityPass=false`
  restores the old speed. Its five tuning numbers are configuration; the
  shipped values are measured ones, so change them only with a score to show.
- **Transcripts are cached** by content hash, keyed with `Transcript.CurrentVersion`.
  Bump that version whenever what is transcribed changes meaning, or stale
  transcripts are resumed as if they were current.
- **DTW word timestamps are why the engine hears one 28 s window at a time.**
  Whisper.net returns only the first 30 s of whatever it is handed with DTW on
  (ADR-0006). Do not "simplify" that away.
- **Windows shell.** PowerShell is the primary shell; a Bash tool is available
  and takes POSIX syntax. Checked-in `.json` files are CRLF (see
  `.gitattributes`), so a `\n`-anchored `sed`/Python replacement will silently
  match nothing — use an editing tool, or account for the line endings.
- Scratch files belong in the session's scratch directory, never in the
  repository or in `/tmp`.
